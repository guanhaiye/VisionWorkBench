using Microsoft.Extensions.Logging;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Domain;
using VisionWorkbench.Infrastructure.Imaging;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

public sealed record StationStartRequest
{
    public required StationEntity Station { get; init; }
    public required Recipe Recipe { get; init; }
    public required long TaskId { get; init; }
    public required string ProjectId { get; init; }
    public required string PhysicalDeviceKey { get; init; }
    public required ICameraSession Camera { get; init; }
    public required IAlgorithmSession Algorithm { get; init; }
    public FrameRoutingStrategy RoutingStrategy { get; init; } = FrameRoutingStrategy.LatestOnly;
}

public sealed record StationOperationResult(long StationId, string StationCode, bool Succeeded, string? Error = null);

/// <summary>多工位运行协调器：每个工位独立维护检测运行、批次和计数状态，独占设备冲突在启动前阻止。</summary>
public sealed class StationRunCoordinator(
    RecordRepository records,
    BatchRepository batches,
    TempImageStore tempImages,
    IResultPublisher? publisher = null,
    ILogger<StationRunCoordinator>? logger = null,
    ILoggerFactory? loggerFactory = null,
    Func<bool>? shouldPersist = null,
    Func<bool>? shouldSaveFullImages = null,
    SopRunRepository? sopRuns = null,
    ISopPendingReplayTrigger? pendingReplayTrigger = null) : IAsyncDisposable
{
    private sealed record ActiveRun(StationStartRequest Request, DetectionRunService Run, BatchService Batch);
    private readonly Dictionary<long, ActiveRun> _active = [];
    private readonly HashSet<long> _startingStations = [];
    private readonly HashSet<long> _stoppingStations = [];
    private readonly HashSet<string> _startingDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public IReadOnlyList<long> RunningStationIds
    {
        get { lock (_gate) return _active.Keys.ToArray(); }
    }

    public async Task<DetectionRunService> StartAsync(
        StationStartRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        lock (_gate)
        {
            if (_active.ContainsKey(request.Station.Id) || !_startingStations.Add(request.Station.Id))
            {
                throw new InvalidOperationException($"工位已在运行: {request.Station.StationCode}");
            }
            if (!_startingDevices.Add(request.PhysicalDeviceKey))
            {
                _startingStations.Remove(request.Station.Id);
                throw new InvalidOperationException($"设备正在被其他工位启动: {request.PhysicalDeviceKey}");
            }
            var conflict = _active.Values.FirstOrDefault(x => string.Equals(
                x.Request.PhysicalDeviceKey, request.PhysicalDeviceKey, StringComparison.OrdinalIgnoreCase));
            if (conflict is not null)
            {
                _startingStations.Remove(request.Station.Id);
                _startingDevices.Remove(request.PhysicalDeviceKey);
                throw new InvalidOperationException($"设备已被工位 {conflict.Request.Station.StationCode} 占用");
            }
        }

        DetectionRunService? run = null;
        try
        {
            run = new DetectionRunService(records, tempImages,
                loggerFactory?.CreateLogger<DetectionRunService>(),
                $"station-{request.Station.Id}", publisher, shouldPersist, shouldSaveFullImages,
                sopRuns: sopRuns,
                pendingReplayTrigger: pendingReplayTrigger);
            var batch = new BatchService(batches);
            var currentBatch = await batch.ResumeOrStartAsync(
                request.TaskId, run.Counting, records, request.ProjectId,
                request.Station.StationCode, cancellationToken);
            await run.StartAsync(request.Recipe, request.TaskId, request.Camera,
                request.Algorithm, currentBatch.Id, request.RoutingStrategy,
                cancellationToken, request.ProjectId);

            lock (_gate)
            {
                _active[request.Station.Id] = new ActiveRun(request, run, batch);
            }
            return run;
        }
        catch
        {
            if (run is not null)
            {
                await CleanupAsync(run.DisposeAsync, "检测运行");
            }
            await CleanupAsync(request.Camera.DisposeAsync, "相机会话");
            await CleanupAsync(request.Algorithm.DisposeAsync, "算法会话");
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _startingStations.Remove(request.Station.Id);
                _startingDevices.Remove(request.PhysicalDeviceKey);
            }
        }
    }

    public async Task<StationOperationResult> StopAsync(
        long stationId, string status = "completed", CancellationToken cancellationToken = default)
    {
        ActiveRun? active;
        lock (_gate)
        {
            if (!_active.TryGetValue(stationId, out active))
                return new StationOperationResult(stationId, "", false, "工位未运行");
            if (!_stoppingStations.Add(stationId))
                return new StationOperationResult(stationId, active.Request.Station.StationCode, false, "工位正在停止");
        }

        var errors = new List<Exception>();
        try
        {
            await active.Run.StopAsync();
            await active.Batch.EndAsync(active.Run.Counting.State.CurrentTotal, status, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "停止工位失败 {Station}", active.Request.Station.StationCode);
            errors.Add(ex);
        }
        finally
        {
            // A failed stop or database write must not skip later resource cleanup.
            await CleanupAsync(active.Run.DisposeAsync, "检测运行", errors);
            await CleanupAsync(active.Request.Camera.DisposeAsync, "相机会话", errors);
            await CleanupAsync(active.Request.Algorithm.DisposeAsync, "算法会话", errors);
            lock (_gate)
            {
                // Keep device ownership until all stop/dispose operations have finished.
                _active.Remove(stationId);
                _stoppingStations.Remove(stationId);
            }
        }

        return new StationOperationResult(stationId, active.Request.Station.StationCode,
            errors.Count == 0, errors.Count == 0 ? null : string.Join("; ", errors.Select(x => x.Message)));
    }

    private async Task CleanupAsync(Func<ValueTask> cleanup, string component, List<Exception>? errors = null)
    {
        try
        {
            await cleanup();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "释放工位资源失败 {Component}", component);
            errors?.Add(ex);
        }
    }

    public async Task<IReadOnlyList<StationOperationResult>> StopAllAsync(
        CancellationToken cancellationToken = default)
    {
        var ids = RunningStationIds;
        return await Task.WhenAll(ids.Select(id => StopAsync(id, "completed", cancellationToken)));
    }

    public async Task<IReadOnlyList<StationOperationResult>> StartAllAsync(
        IEnumerable<StationStartRequest> requests, CancellationToken cancellationToken = default)
    {
        var results = new List<StationOperationResult>();
        foreach (var request in requests)
        {
            try
            {
                await StartAsync(request, cancellationToken);
                results.Add(new StationOperationResult(request.Station.Id, request.Station.StationCode, true));
            }
            catch (Exception ex)
            {
                results.Add(new StationOperationResult(request.Station.Id, request.Station.StationCode, false, ex.Message));
            }
        }
        return results;
    }

    private static void ValidateRequest(StationStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Station.IsArchived || !request.Station.Enabled)
        {
            throw new InvalidOperationException($"工位未启用或已归档: {request.Station.StationCode}");
        }
        if (string.IsNullOrWhiteSpace(request.Station.StationCode)
            || string.IsNullOrWhiteSpace(request.ProjectId)
            || string.IsNullOrWhiteSpace(request.PhysicalDeviceKey))
        {
            throw new ArgumentException("工位编号、项目编号和设备标识不能为空");
        }
    }

    public async ValueTask DisposeAsync() => await StopAllAsync();
}
