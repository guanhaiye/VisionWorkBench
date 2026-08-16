using Microsoft.Extensions.Logging;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.Application;

/// <summary>相机注册表：聚合全部 Provider 的发现与会话创建（文档 §10）。</summary>
public sealed class CameraRegistry(IEnumerable<ICameraProvider> providers, ILogger<CameraRegistry>? logger = null)
{
    private readonly IReadOnlyList<ICameraProvider> _providers = providers.ToArray();

    public IReadOnlyList<ICameraProvider> Providers => _providers;

    public ICameraProvider Resolve(string providerId) =>
        _providers.FirstOrDefault(p => p.ProviderId == providerId)
        ?? throw new KeyNotFoundException($"未注册的相机 Provider: {providerId}");

    public async Task<IReadOnlyList<CameraDescriptor>> DiscoverAllAsync(CancellationToken cancellationToken)
    {
        var all = new List<CameraDescriptor>();
        foreach (var provider in _providers)
        {
            try
            {
                var found = await provider.DiscoverAsync(cancellationToken);
                all.AddRange(found);
                logger?.LogInformation("Provider {Provider} 发现 {Count} 台设备",
                    provider.ProviderId, found.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个 Provider 失败不阻塞其他（如无摄像头机器上 DSHOW）
                logger?.LogWarning(ex, "Provider {Provider} 发现失败", provider.ProviderId);
            }
        }
        return all;
    }

    public Task<ICameraSession> OpenSessionAsync(
        CameraDescriptor descriptor, CameraOpenOptions options, CancellationToken cancellationToken)
    {
        var provider = Resolve(descriptor.ProviderId);
        return provider.CreateSessionAsync(descriptor, cancellationToken);
    }
}
