using System.Net.Http.Json;
using System.Text.Json;
using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Application;

/// <summary>对外结果发布：本地 JSONL 必达，Webhook 可选（STA-011/DAT-006）。</summary>
public interface IResultPublisher
{
    Task PublishAsync(ResultEnvelope envelope, CancellationToken cancellationToken = default);
    Task PublishProductAsync(ProductResultEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>
/// 无算法帧时的产品级最终结果。它明确关联 SOP 周期，不伪造 AlgorithmOutput。
/// </summary>
public sealed record ProductResultEnvelope
{
    public string SchemaVersion { get; init; } = "1.0";
    public required string ResultId { get; init; }
    public string Kind { get; init; } = "product-final";
    public required string ProjectId { get; init; }
    public required string StationCode { get; init; }
    public long TaskId { get; init; }
    public long? BatchId { get; init; }
    public long? SopRunId { get; init; }
    public required string CycleId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required DecisionResult Decision { get; init; }
    public required string WorkflowResultJson { get; init; }
}

public sealed class ResultPublisher : IResultPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _jsonlPath;
    private readonly Uri? _webhook;
    private readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ResultPublisher(string jsonlPath, string? webhookUrl = null)
    {
        _jsonlPath = Path.GetFullPath(jsonlPath);
        _webhook = Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) ? uri : null;
        Directory.CreateDirectory(Path.GetDirectoryName(_jsonlPath)!);
    }

    public async Task PublishAsync(ResultEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await PublishJsonAsync(envelope, cancellationToken);
    }

    public async Task PublishProductAsync(ProductResultEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 本地 JSONL 是可重放的持久化落点：同一 ResultId 已落盘时不再追加，
            // 但 webhook 仍会在下方再次投递，以支持“本地成功、远端失败”的重试。
            if (!LocalProductResultExists(envelope.ResultId))
            {
                await File.AppendAllTextAsync(_jsonlPath, json + Environment.NewLine, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (_webhook is not null)
        {
            using var response = await _httpClient.PostAsJsonAsync(_webhook, envelope, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    private bool LocalProductResultExists(string resultId)
    {
        if (!File.Exists(_jsonlPath))
        {
            return false;
        }
        foreach (var line in File.ReadLines(_jsonlPath))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("kind", out var kind)
                    && kind.GetString() == "product-final"
                    && root.TryGetProperty("resultId", out var id)
                    && id.GetString() == resultId)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // 保留历史坏行，不影响后续结果扫描。
            }
        }
        return false;
    }

    private async Task PublishJsonAsync<T>(T envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_jsonlPath, json + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        if (_webhook is not null)
        {
            using var response = await _httpClient.PostAsJsonAsync(_webhook, envelope, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        _gate.Dispose();
        await Task.CompletedTask;
    }
}
