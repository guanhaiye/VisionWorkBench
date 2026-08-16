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
        if (string.IsNullOrWhiteSpace(recipe.CameraProviderId) || string.IsNullOrWhiteSpace(recipe.CameraDeviceId))
        {
            throw new ArgumentException("必须选择相机（CFG-004）");
        }
        if (string.IsNullOrWhiteSpace(recipe.PluginId))
        {
            throw new ArgumentException("必须选择算法插件（CFG-004）");
        }
        if (recipe.Rules.Count == 0)
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
        entity.Description = recipe.Description;
        entity.CameraProviderId = recipe.CameraProviderId;
        entity.CameraDeviceId = recipe.CameraDeviceId;
        entity.PluginId = recipe.PluginId;
        entity.PluginVersion = recipe.PluginVersion;
        entity.SettingsJson = string.IsNullOrWhiteSpace(recipe.SettingsJson) ? "{}" : recipe.SettingsJson;
        entity.RulesJson = JsonSerializer.Serialize(recipe.Rules, JsonOptions);
        entity.RegionsJson = recipe.Roi is null
            ? null
            : JsonSerializer.Serialize(new { recipe.Roi, Policy = recipe.RoiPolicy.ToString() }, JsonOptions);
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
            }
            catch (JsonException)
            {
                // 区域损坏退回整幅图
            }
        }
        return new Recipe
        {
            Name = entity.Name,
            Description = entity.Description ?? "",
            CameraProviderId = entity.CameraProviderId,
            CameraDeviceId = entity.CameraDeviceId,
            PluginId = entity.PluginId,
            PluginVersion = entity.PluginVersion,
            SettingsJson = entity.SettingsJson,
            Roi = roi,
            RoiPolicy = policy,
            Rules = rules,
        };
    }

    private sealed record RegionDoc(
        Contracts.Results.NormalizedRect? Roi, string? Policy);
}
