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
        ValidateGeometry(recipe);
        ValidateSettingsJson(recipe.SettingsJson);
        // 流水计数模式无 OK/NG 判定，允许零规则（§16.3/§16.4）
        if (recipe.Rules.Count == 0 && recipe.CountingMode == CountingMode.Snapshot)
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
        var hasRegions = recipe.Roi is not null
            || recipe.CountingLine is not null
            || recipe.CountingMode != CountingMode.Snapshot
            || recipe.ExecutionProvider != "cpu";
        entity.RegionsJson = hasRegions
            ? JsonSerializer.Serialize(new
            {
                recipe.Roi,
                Policy = recipe.Roi is null ? null : recipe.RoiPolicy.ToString(),
                Mode = recipe.CountingMode.ToString(),
                Line = recipe.CountingLine,
                ExecutionProvider = recipe.ExecutionProvider,
            }, JsonOptions)
            : null;
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
        var mode = CountingMode.Snapshot;
        CountingLineConfig? line = null;
        var executionProvider = "cpu";
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
                if (doc?.Mode is { } m)
                {
                    mode = m; // JsonStringEnumConverter 已校验，非法值抛 JsonException 走回退
                }
                line = doc?.Line;
                executionProvider = doc?.ExecutionProvider is "cuda" ? "cuda" : "cpu";
            }
            catch (JsonException)
            {
                // 区域损坏退回整幅图 + 快照模式
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
            CountingMode = mode,
            CountingLine = line,
            Rules = rules,
        };
    }

    private sealed record RegionDoc(
        Contracts.Results.NormalizedRect? Roi,
        string? Policy,
        CountingMode? Mode,
        CountingLineConfig? Line,
        string? ExecutionProvider);

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
            return;
        }
        if (!IsValidPoint(line.A) || !IsValidPoint(line.B)
            || (Math.Abs(line.A.X - line.B.X) < 1e-9 && Math.Abs(line.A.Y - line.B.Y) < 1e-9)
            || line.Hysteresis < 0 || line.Hysteresis > 0.2)
        {
            throw new ArgumentException("检测线端点必须位于 0~1、两端不能重合，滞回宽度须为 0~0.2（CFG-006）");
        }
    }

    private static bool IsValidRect(Contracts.Results.NormalizedRect rect) =>
        rect.X >= 0 && rect.Y >= 0 && rect.Width > 0 && rect.Height > 0
        && rect.X + rect.Width <= 1 && rect.Y + rect.Height <= 1;

    private static bool IsValidPoint(Contracts.Results.NormalizedPoint point) =>
        point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1;
}
