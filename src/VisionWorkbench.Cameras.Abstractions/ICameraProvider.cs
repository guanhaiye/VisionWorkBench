namespace VisionWorkbench.Cameras.Abstractions;

/// <summary>相机 Provider 抽象工厂（文档 §10.3）。</summary>
public interface ICameraProvider
{
    string ProviderId { get; }
    string DisplayName { get; }

    Task<IReadOnlyList<CameraDescriptor>> DiscoverAsync(CancellationToken cancellationToken);

    Task<ICameraSession> CreateSessionAsync(
        CameraDescriptor descriptor,
        CancellationToken cancellationToken);
}
