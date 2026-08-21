using System.Net.Http.Json;
using System.Text.Json;
using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Application;

/// <summary>对外结果发布：本地 JSONL 必达，Webhook 可选（STA-011/DAT-006）。</summary>
public interface IResultPublisher
{
    Task PublishAsync(ResultEnvelope envelope, CancellationToken cancellationToken = default);
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
