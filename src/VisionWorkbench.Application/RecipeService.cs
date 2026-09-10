using System.Text.Json;
using VisionWorkbench.Domain;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

/// <summary>配方服务：TaskEntity ↔ Recipe 领域模型映射与校验（CFG-004）。</summary>
public sealed class RecipeService(TaskRepository tasks)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<IReadOnlyList<(TaskEntity Entity, Recipe Recipe)>> ListAsync(
        CancellationToken ct = default)
    {
        var entities = await tasks.ListAsync(ct);
        return entities
            .Select(e => (e, ToRecipe(e)))
            .Where(pair => pair.Item2 is not null)
            .Select(pair => (pair.Item1, pair.Item2!))
            .ToArray();
    }

    public async Task<(TaskEntity Entity, Recipe Recipe)?> FindAsync(long id, CancellationToken ct = default)
    {
        var entity = await tasks.FindAsync(id, ct);
        if (entity is null)
        {
            return null;
        }
        var recipe = ToRecipe(entity);
        return recipe is null ? null : (entity, recipe);
    }

    /// <summary>保存：校验规则 JSON 可序列化、相机与插件非空（CFG-004）。</summary>
    public async Task<TaskEntity> SaveAsync(Recipe recipe, long? existingId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (string.IsNullOrWhiteSpace(recipe.Name))
        {
            throw new ArgumentException("任务名不能为空（CFG-004）");
        }
        if (recipe.StationCode.Length > 64)
        {
            throw new ArgumentException("工位编号不能超过 64 个字符");
        }
        if (string.IsNullOrWhiteSpace(recipe.CameraProviderId) || string.IsNullOrWhiteSpace(recipe.CameraDeviceId))
        {
            throw new ArgumentException("必须选择相机（CFG-004）");
        }
        if (string.IsNullOrWhiteSpace(recipe.PluginId))
        {
            throw new ArgumentException("必须选择算法插件（CFG-004）");
        }
        if (recipe.ExecutionProvider is not ("cpu" or "cuda"))
        {
            throw new ArgumentException("推理设备只能选择 cpu 或 cuda（CFG-006）");
        }
        var taskType = recipe.TaskType.Normalize();
        ValidateGeometry(recipe);
        ValidateSop(recipe.Sop);
        ValidateSettingsJson(recipe.SettingsJson);
        // 流水计数模式和轮廓分析不需要 OK/NG 判定，允许零规则（§16.3/§16.4）
        if (recipe.Rules.Count == 0
            && recipe.CountingMode == CountingMode.Snapshot
            && taskType is (InspectionTaskType.Detection or InspectionTaskType.BehaviorRecognition)
            && !(recipe.PostProcess.Mode == PostProcessMode.PythonScript
                 && !string.IsNullOrWhiteSpace(recipe.PostProcess.Script)))
        {
            throw new ArgumentException("至少配置一条判定规则（CFG-004）");
        }
        var duplicateIds = recipe.Rules
            .GroupBy(r => r.RuleId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateIds is not null)
        {
            throw new ArgumentException($"规则 ID 重复: {duplicateIds.Key}（CFG-004）");
        }

        var entity = existingId is { } id
            ? await tasks.FindAsync(id, ct) ?? new TaskEntity { Id = id }
            : new TaskEntity();
        entity.Name = recipe.Name;
        entity.StationCode = string.IsNullOrWhiteSpace(recipe.StationCode)
            ? $"ST-{Guid.NewGuid():N}"[..11].ToUpperInvariant()
            : recipe.StationCode.Trim();
        entity.Description = recipe.Description;
        entity.CameraProviderId = recipe.CameraProviderId;
        entity.CameraDeviceId = recipe.CameraDeviceId;
        entity.PluginId = recipe.PluginId;
        entity.PluginVersion = recipe.PluginVersion;
        entity.SettingsJson = string.IsNullOrWhiteSpace(recipe.SettingsJson) ? "{}" : recipe.SettingsJson;
        entity.RulesJson = JsonSerializer.Serialize(recipe.Rules, JsonOptions);
        // ROI / 计数模式 / 检测线 共存于 RegionsJson（可选字段，旧数据缺失回退默认）
        var hasRegions = !taskType.IsCounting()
            || recipe.Roi is not null
            || recipe.CountingLine is not null
            || recipe.CountingMode != CountingMode.Snapshot
            || recipe.ExecutionProvider != "cpu"
            || recipe.Behavior.Enabled
            || recipe.Behavior.Zones.Count > 0
            || recipe.PostProcess.Mode != PostProcessMode.VisualRules
            || !string.IsNullOrWhiteSpace(recipe.PostProcess.Script);
        entity.RegionsJson = hasRegions
            ? JsonSerializer.Serialize(new
            {
                recipe.Roi,
                Policy = recipe.Roi is null ? null : recipe.RoiPolicy.ToString(),
                // 原样持久化以保证旧任务（Counting/ContourAnalysis）可无损往返；
                // 业务判断通过 Normalize/IsXxx 扩展方法获得统一语义。
                TaskType = recipe.TaskType.ToString(),
                Mode = recipe.CountingMode.ToString(),
                Line = recipe.CountingLine,
                ExecutionProvider = recipe.ExecutionProvider,
                Behavior = recipe.Behavior,
                PostProcess = recipe.PostProcess,
            }, JsonOptions)
            : null;
        entity.WorkflowJson = recipe.Sop is null
            ? null
            : JsonSerializer.Serialize(recipe.Sop, JsonOptions);
        return await tasks.SaveAsync(entity, ct);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken ct = default) => tasks.DeleteAsync(id, ct);

    public static Recipe? ToRecipe(TaskEntity entity)
    {
        IReadOnlyList<InspectionRule> rules;
        try
        {
            rules = JsonSerializer.Deserialize<InspectionRule[]>(entity.RulesJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return null; // 规则 JSON 损坏的任务视为不可用
        }
        Contracts.Results.NormalizedRect? roi = null;
        var policy = RoiBoundaryPolicy.CenterInside;
        var taskType = InspectionTaskType.Detection;
        var mode = CountingMode.Snapshot;
        CountingLineConfig? line = null;
        var executionProvider = "cpu";
        var behavior = new BehaviorRecognitionConfig();
        var postProcess = new PostProcessConfig();
        SopBinding? sop = null;
        if (!string.IsNullOrWhiteSpace(entity.RegionsJson))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<RegionDoc>(entity.RegionsJson, JsonOptions);
                roi = doc?.Roi;
                if (doc?.Policy is { } p && Enum.TryParse<RoiBoundaryPolicy>(p, ignoreCase: true, out var parsed))
                {
                    policy = parsed;
                }
                if (doc?.TaskType is { } t)
                {
                    taskType = t;
                }
                if (doc?.Mode is { } m)
                {
                    mode = m; // JsonStringEnumConverter 已校验，非法值抛 JsonException 走回退
                }
                line = doc?.Line;
                executionProvider = doc?.ExecutionProvider is "cuda" ? "cuda" : "cpu";
                behavior = doc?.Behavior ?? new BehaviorRecognitionConfig();
                postProcess = doc?.PostProcess ?? new PostProcessConfig();
            }
            catch (JsonException)
            {
                // 区域损坏退回整幅图 + 快照模式
            }
        }
        if (!string.IsNullOrWhiteSpace(entity.WorkflowJson))
        {
            try
            {
                sop = JsonSerializer.Deserialize<SopBinding>(entity.WorkflowJson, JsonOptions);
                ValidateSop(sop);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return new Recipe
        {
            StationCode = string.IsNullOrWhiteSpace(entity.StationCode) ? $"ST-{entity.Id:000}" : entity.StationCode,
            Name = entity.Name,
            Description = entity.Description ?? "",
            CameraProviderId = entity.CameraProviderId,
            CameraDeviceId = entity.CameraDeviceId,
            PluginId = entity.PluginId,
            PluginVersion = entity.PluginVersion,
            ExecutionProvider = executionProvider,
            SettingsJson = entity.SettingsJson,
            Roi = roi,
            RoiPolicy = policy,
            TaskType = taskType,
            CountingMode = mode,
            CountingLine = line,
            Behavior = behavior,
            PostProcess = postProcess,
            Sop = sop,
            Rules = rules,
        };
    }

    private sealed record RegionDoc(
        Contracts.Results.NormalizedRect? Roi,
        string? Policy,
        InspectionTaskType? TaskType,
        CountingMode? Mode,
        CountingLineConfig? Line,
        string? ExecutionProvider,
        BehaviorRecognitionConfig? Behavior,
        PostProcessConfig? PostProcess);

    private static void ValidateSettingsJson(string settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return;
        }
        try
        {
            if (JsonDocument.Parse(settingsJson).RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("插件参数必须是 JSON 对象（CFG-006）");
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"插件参数不是合法 JSON（CFG-006）: {ex.Message}", ex);
        }
    }

    private static void ValidateGeometry(Recipe recipe)
    {
        if (recipe.Roi is { } roi && !IsValidRect(roi))
        {
            throw new ArgumentException("ROI 必须位于 0~1 且宽高大于 0（CFG-006）");
        }
        if (recipe.CountingLine is not { } line)
        {
            ValidateBehavior(recipe.Behavior);
            return;
        }
        if (!IsValidPoint(line.A) || !IsValidPoint(line.B)
            || (Math.Abs(line.A.X - line.B.X) < 1e-9 && Math.Abs(line.A.Y - line.B.Y) < 1e-9)
            || line.Hysteresis < 0 || line.Hysteresis > 0.2)
        {
            throw new ArgumentException("检测线端点必须位于 0~1、两端不能重合，滞回宽度须为 0~0.2（CFG-006）");
        }
        ValidateBehavior(recipe.Behavior);
    }

    private static void ValidateBehavior(BehaviorRecognitionConfig behavior)
    {
        foreach (var zone in behavior.Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Id) || zone.Polygon.Count < 3
                || zone.Polygon.Any(point => !IsValidPoint(point)))
            {
                throw new ArgumentException("行为识别区域必须包含至少 3 个、且位于 0~1 的多边形点（CFG-006）。");
            }
        }

        foreach (var settings in new[] { behavior.Intrusion, behavior.Loitering, behavior.Crowding, behavior.Fall })
        {
            if (settings.MinimumConfidence is < 0 or > 1
                || settings.ConfirmingSeconds < 0
                || settings.RecoverySeconds < 0
                || settings.CooldownSeconds < 0
                || settings.WarningSeconds < 0
                || settings.AlarmSeconds < 0
                || settings.WarningCount < 1
                || settings.AlarmCount < 1
                || settings.MaximumMissingSeconds < 0)
            {
                throw new ArgumentException("行为识别阈值必须为有效的非负数，置信度必须在 0~1 之间（CFG-006）。");
            }
        }
    }

    private static void ValidateSop(SopBinding? binding)
    {
        if (binding is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(binding.DefinitionId)
            || binding.Version < 1
            || binding.Definition is null
            || binding.Definition.Steps.Count == 0)
        {
            throw new JsonException("SOP 绑定缺少已发布定义或步骤");
        }
        var steps = binding.Definition.Steps.OrderBy(step => step.Order).ToArray();
        if (steps.Any(step => string.IsNullOrWhiteSpace(step.Id)
                              || string.IsNullOrWhiteSpace(step.Name)
                              || step.Order < 1
                              || step.MinimumStableFrames < 1
                              || step.TimeoutSeconds < 0
                              || step.Conditions.Count == 0))
        {
            throw new JsonException("SOP 步骤配置无效");
        }
        if (steps.Select(step => step.Order).Distinct().Count() != steps.Length
            || steps.SelectMany(step => step.Conditions).Any(condition => string.IsNullOrWhiteSpace(condition.Id)))
        {
            throw new JsonException("SOP 步骤顺序或条件 ID 重复");
        }
    }

    private static bool IsValidRect(Contracts.Results.NormalizedRect rect) =>
        rect.X >= 0 && rect.Y >= 0 && rect.Width > 0 && rect.Height > 0
        && rect.X + rect.Width <= 1 && rect.Y + rect.Height <= 1;

    private static bool IsValidPoint(Contracts.Results.NormalizedPoint point) =>
        point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1;
}
