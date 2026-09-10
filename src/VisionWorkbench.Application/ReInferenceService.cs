using System.Text.Json;
using VisionWorkbench.Algorithms;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

/// <summary>
/// 使用新模型重新分析历史图片。新运行写入新记录，保留来源记录和原始结果（DAT-005）。
/// </summary>
public sealed class ReInferenceService(
    RecordRepository records,
    RecipeService recipes,
    AlgorithmManager algorithms)
{
    public async Task<InspectionRecordEntity> RunAsync(
        long sourceRecordId,
        string pluginId,
        string settingsJson,
        string modelVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException("重新推理必须指定插件");
        }
        if (string.IsNullOrWhiteSpace(modelVersion))
        {
            throw new ArgumentException("重新推理必须填写模型版本");
        }

        var source = await records.FindAsync(sourceRecordId, cancellationToken)
            ?? throw new InvalidOperationException($"源记录不存在: {sourceRecordId}");
        if (string.IsNullOrWhiteSpace(source.OriginalImagePath) || !File.Exists(source.OriginalImagePath))
        {
            throw new FileNotFoundException("源记录图片不存在，无法重新推理", source.OriginalImagePath);
        }
        var recipePair = await recipes.FindAsync(source.TaskId, cancellationToken)
            ?? throw new InvalidOperationException($"源记录关联任务不存在: {source.TaskId}");
        var recipe = recipePair.Recipe;
        using var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson);

        await using var session = await algorithms.CreateSessionAsync(pluginId, cancellationToken);
        await session.InitializeAsync(new AlgorithmInitialization
        {
            Settings = settings.RootElement.Clone(),
        }, cancellationToken);
        await session.StartAsync(new AlgorithmStartOptions
        {
            Mode = "snapshot",
            Roi = recipe.Roi,
        }, cancellationToken);

        var started = DateTimeOffset.UtcNow;
        var output = await session.SubmitAsync(new AlgorithmInput
        {
            InputId = $"re-{sourceRecordId}-{Guid.NewGuid():N}",
            ImagePath = source.OriginalImagePath,
            CapturedAt = started,
            Roi = recipe.Roi,
        }, cancellationToken);
        var decision = RuleEngine.Evaluate(output, recipe.Rules, recipe.Roi, recipe.RoiPolicy);
        var result = new InspectionRecordEntity
        {
            ProjectId = source.ProjectId,
            StationCode = source.StationCode,
            TaskId = source.TaskId,
            BatchId = source.BatchId,
            SourceRecordId = source.Id,
            RunType = "re_inference",
            StartedAt = started.UtcDateTime,
            CompletedAt = DateTime.UtcNow,
            Status = decision.Status switch
            {
                DecisionStatus.Ok => "ok",
                DecisionStatus.Ng => "ng",
                DecisionStatus.ReviewRequired => "review_required",
                DecisionStatus.Processing => "processing",
                DecisionStatus.Unknown => "processing",
                _ => "error",
            },
            OriginalImagePath = source.OriginalImagePath,
            PluginVersion = pluginId,
            ModelVersion = modelVersion.Trim(),
            AlgorithmElapsedMs = output.Performance?.TotalMs ?? 0,
            RawResultJson = JsonSerializer.Serialize(new ResultEnvelope
            {
                ProjectId = source.ProjectId,
                StationCode = source.StationCode,
                TaskId = source.TaskId,
                BatchId = source.BatchId,
                Timestamp = output.Timestamp,
                Output = output,
            }),
            FinalResultJson = JsonSerializer.Serialize(new ResultEnvelope
            {
                ProjectId = source.ProjectId,
                StationCode = source.StationCode,
                TaskId = source.TaskId,
                BatchId = source.BatchId,
                Timestamp = output.Timestamp,
                Decision = decision,
                Output = output,
            }),
        };
        await session.StopAsync(CancellationToken.None);
        return await records.AddAsync(result, output.CountingEvents, output.Events, cancellationToken);
    }
}
