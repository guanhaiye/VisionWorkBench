using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace VisionWorkbench.Persistence;

public enum CommunicationRequestState { New, Cached, Conflict, Processing }
public sealed record CommunicationRequestResult(CommunicationRequestState State, string? ResponseJson, CommunicationRequestEntity? Request);

public sealed class CommunicationRequestStore(VisionDbContextFactory factory)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<CommunicationRequestResult> BeginAsync(string projectCode, string clientId, string requestId,
        string command, string requestBody, CancellationToken cancellationToken = default)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestBody)));
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var existing = await db.CommunicationRequests.FirstOrDefaultAsync(item =>
                item.ProjectCode == projectCode && item.ClientId == clientId && item.RequestId == requestId, cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestHash, hash, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.Command, command, StringComparison.Ordinal))
                    return new CommunicationRequestResult(CommunicationRequestState.Conflict, null, existing);
                if (existing.Status == "completed" && existing.ExpiresAtUtc > DateTime.UtcNow)
                    return new CommunicationRequestResult(CommunicationRequestState.Cached, existing.ResponseJson, existing);
                if (existing.Status == "processing")
                    return new CommunicationRequestResult(CommunicationRequestState.Processing, null, existing);
            }
            var request = existing ?? new CommunicationRequestEntity
            {
                ProjectCode = projectCode, ClientId = clientId, RequestId = requestId,
                Command = command, RequestHash = hash, ReceivedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            };
            request.ReceivedAtUtc = DateTime.UtcNow;
            request.ExpiresAtUtc = request.ReceivedAtUtc.AddDays(7);
            request.Command = command;
            request.RequestHash = hash;
            request.Status = "processing";
            request.ResponseJson = null;
            request.CompletedAtUtc = null;
            if (existing is null) db.CommunicationRequests.Add(request);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CommunicationRequestResult(CommunicationRequestState.New, null, request);
        }
        finally { Gate.Release(); }
    }

    public async Task CompleteAsync(long requestId, string responseJson, CancellationToken cancellationToken = default)
    {
        await using var db = factory.CreateDbContext();
        var request = await db.CommunicationRequests.SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken)
            ?? throw new InvalidOperationException("通信请求不存在");
        request.Status = "completed";
        request.ResponseJson = responseJson;
        request.CompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> PurgeExpiredAsync(DateTime? nowUtc = null, CancellationToken cancellationToken = default)
    {
        await using var db = factory.CreateDbContext();
        var cutoff = nowUtc ?? DateTime.UtcNow;
        return await db.CommunicationRequests.Where(item => item.Status != "processing" && item.ExpiresAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
