using Microsoft.Extensions.Logging;
using VisionWorkbench.Application;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

public sealed class TcpTaskExecutionService(AppServices services)
{
    public async Task<TcpTaskExecutionResult> ExecuteAsync(TaskEntity task, CancellationToken cancellationToken)
    {
        if (services.LiveTaskTriggerExecutor is not null)
        {
            return await services.LiveTaskTriggerExecutor(task.Id, cancellationToken);
        }

        // 实时检测页面尚未加载时保留后台单帧执行能力，确保 TCP 服务不依赖页面是否可见。
        return await ExecuteStandaloneAsync(task, cancellationToken);
    }

    private async Task<TcpTaskExecutionResult> ExecuteStandaloneAsync(TaskEntity task, CancellationToken cancellationToken)
    {
        var found = await services.Recipes.FindAsync(task.Id, cancellationToken)
            ?? throw new InvalidOperationException($"任务不存在或配置无效: {task.Name}");
        var recipe = found.Recipe;
        var descriptor = new CameraDescriptor
        {
            ProviderId = recipe.CameraProviderId,
            DeviceId = recipe.CameraDeviceId,
            DisplayName = recipe.CameraDeviceId,
        };
        var options = services.ApplyCameraDefaults(descriptor,
            new CameraOpenOptions { FrameIntervalMs = 50, Loop = false, MaxFrames = 1, Parameters = recipe.CameraParameters });
        var camera = await services.Cameras.OpenSessionAsync(descriptor, options, cancellationToken);
        var algorithm = await services.AlgorithmManager.CreateSessionAsync(recipe.PluginId, cancellationToken);
        var run = new DetectionRunService(services.Records, services.TempImages,
            services.LoggerFactory.CreateLogger<DetectionRunService>(), $"tcp-task-{task.Id}", services.ResultPublisher,
            () => services.Settings.EnableHistory,
            () => services.Settings.EnableHistory,
            sopRuns: services.SopRuns,
            pendingReplayTrigger: services.SopProductResultReplayer);
        var batchService = new BatchService(services.Batches);
        var completion = new TaskCompletionSource<RecordCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.RecordCompleted += (_, result) => completion.TrySetResult(result);
        run.Faulted += (_, fault) => completion.TrySetException(new InvalidOperationException($"{fault.Code}: {fault.Message}"));
        try
        {
            await camera.OpenAsync(options, cancellationToken);
            var batch = await batchService.ResumeOrStartAsync(task.Id, run.Counting, services.Records,
                services.Settings.DataDirectory, task.StationCode, cancellationToken);
            await run.StartAsync(recipe, task.Id, camera, algorithm, batch.Id,
                FrameRoutingStrategy.Bounded, cancellationToken);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            await run.StopAsync();
            await batchService.EndAsync(result.CountAfter, "completed", cancellationToken);
            return new TcpTaskExecutionResult("completed", result.CountAfter,
                result.Decision.Status.ToString(), result.Record.Id);
        }
        finally
        {
            await run.DisposeAsync();
            await camera.DisposeAsync();
            await algorithm.DisposeAsync();
        }
    }
}
