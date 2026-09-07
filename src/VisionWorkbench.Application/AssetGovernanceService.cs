using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.Application;

/// <summary>配方、模型和部署绑定的不可变版本登记与发布门禁。</summary>
public sealed class AssetGovernanceService(VisionDbContextFactory factory, AuditService? audit = null, AccessControlService? access = null)
{
    public async Task<RecipeVersionEntity> SaveRecipeDraftAsync(string recipeCode, string definitionJson, string actor, CancellationToken ct = default)
    {
        await RequireAsync(actor, "recipe.edit", ct);
        ValidateJson(definitionJson);
        await using var db = factory.CreateDbContext();
        var next = (await db.RecipeVersions.Where(x => x.RecipeCode == recipeCode).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1;
        var entity = new RecipeVersionEntity { RecipeCode = recipeCode, Version = next, DefinitionJson = definitionJson, ContentSha256 = Hash(definitionJson) };
        db.RecipeVersions.Add(entity); await db.SaveChangesAsync(ct);
        if (audit is not null) await audit.RecordAsync("recipe.draft", "recipe", recipeCode, actor, detailsJson: $"{{\"version\":{next}}}", cancellationToken: ct);
        return entity;
    }

    public async Task PublishRecipeAsync(string recipeCode, int version, string actor, CancellationToken ct = default)
    {
        await RequireAsync(actor, "recipe.publish", ct);
        await using var db = factory.CreateDbContext();
        var item = await db.RecipeVersions.SingleOrDefaultAsync(x => x.RecipeCode == recipeCode && x.Version == version, ct) ?? throw new InvalidOperationException("配方版本不存在");
        ValidateJson(item.DefinitionJson);
        item.State = "published"; item.PublishedBy = actor; item.PublishedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (audit is not null) await audit.RecordAsync("recipe.publish", "recipe", recipeCode, actor, detailsJson: $"{{\"version\":{version}}}", cancellationToken: ct);
    }

    public async Task<ModelArtifactEntity> RegisterModelAsync(string modelCode, string filePath, string algorithm, string actor, CancellationToken ct = default)
    {
        await RequireAsync(actor, "model.manage", ct);
        if (!File.Exists(filePath)) throw new FileNotFoundException("模型文件不存在", filePath);
        await using var db = factory.CreateDbContext();
        var next = (await db.ModelArtifacts.Where(x => x.ModelCode == modelCode).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1;
        var item = new ModelArtifactEntity { ModelCode = modelCode, Version = next, FilePath = Path.GetFullPath(filePath), Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(filePath, ct))), Algorithm = algorithm };
        db.ModelArtifacts.Add(item); await db.SaveChangesAsync(ct);
        if (audit is not null) await audit.RecordAsync("model.register", "model", modelCode, actor, detailsJson: $"{{\"version\":{next},\"sha256\":\"{item.Sha256}\"}}", cancellationToken: ct);
        return item;
    }

    public async Task PublishModelAsync(string modelCode, int version, string actor, CancellationToken ct = default)
    {
        await RequireAsync(actor, "model.manage", ct);
        await using var db = factory.CreateDbContext();
        var item = await db.ModelArtifacts.SingleOrDefaultAsync(x => x.ModelCode == modelCode && x.Version == version, ct) ?? throw new InvalidOperationException("模型版本不存在");
        if (!File.Exists(item.FilePath) || !string.Equals(item.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(item.FilePath, ct))), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模型文件校验失败");
        item.State = "published"; item.PublishedBy = actor; item.PublishedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct);
        if (audit is not null) await audit.RecordAsync("model.publish", "model", modelCode, actor,
            detailsJson: $"{{\"version\":{version},\"sha256\":\"{item.Sha256}\"}}", cancellationToken: ct);
    }

    public async Task BindAsync(string targetCode, string recipeCode, int recipeVersion, string modelCode, int modelVersion, string actor, CancellationToken ct = default)
    {
        await RequireAsync(actor, "production.run", ct);
        await using var db = factory.CreateDbContext();
        if (!await db.RecipeVersions.AnyAsync(x => x.RecipeCode == recipeCode && x.Version == recipeVersion && x.State == "published", ct)) throw new InvalidOperationException("部署前配方必须已发布");
        if (!await db.ModelArtifacts.AnyAsync(x => x.ModelCode == modelCode && x.Version == modelVersion && x.State == "published", ct)) throw new InvalidOperationException("部署前模型必须已发布");
        var item = await db.DeploymentBindings.SingleOrDefaultAsync(x => x.TargetCode == targetCode, ct) ?? new DeploymentBindingEntity { TargetCode = targetCode };
        item.RecipeCode = recipeCode; item.RecipeVersion = recipeVersion; item.ModelCode = modelCode; item.ModelVersion = modelVersion; item.UpdatedBy = actor; item.UpdatedAtUtc = DateTime.UtcNow; item.State = "active";
        if (item.Id == 0) db.DeploymentBindings.Add(item); await db.SaveChangesAsync(ct);
        if (audit is not null) await audit.RecordAsync("deployment.bind", "target", targetCode, actor, detailsJson: $"{{\"recipeVersion\":{recipeVersion},\"modelVersion\":{modelVersion}}}", cancellationToken: ct);
    }

    public async Task RetireRecipeAsync(string recipeCode, int version, string actor, CancellationToken ct = default)
    {
        await RequireAsync(actor, "recipe.edit", ct);
        await using var db = factory.CreateDbContext();
        var item = await db.RecipeVersions.SingleOrDefaultAsync(x => x.RecipeCode == recipeCode && x.Version == version, ct)
            ?? throw new InvalidOperationException("Recipe version does not exist");
        if (item.State == "retired" || item.State == "rolledback") return;
        item.State = "retired";
        await db.SaveChangesAsync(ct);
        if (audit is not null) await audit.RecordAsync("recipe.retire", "recipe", recipeCode, actor,
            detailsJson: $"{{\"version\":{version}}}", cancellationToken: ct);
    }

    public async Task RollbackRecipeAsync(string recipeCode, int version, string actor, CancellationToken ct = default)
    {
        await RequireAsync(actor, "recipe.rollback", ct);
        await using var db = factory.CreateDbContext();
        var item = await db.RecipeVersions.SingleOrDefaultAsync(x => x.RecipeCode == recipeCode && x.Version == version, ct)
            ?? throw new InvalidOperationException("Recipe version does not exist");
        item.State = "rolledback";
        await db.SaveChangesAsync(ct);
        if (audit is not null) await audit.RecordAsync("recipe.rollback", "recipe", recipeCode, actor,
            detailsJson: $"{{\"version\":{version}}}", cancellationToken: ct);
    }

    public async Task<string> ExportRecipeAsync(string recipeCode, int version, CancellationToken ct = default)
    {
        await using var db = factory.CreateDbContext();
        var item = await db.RecipeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.RecipeCode == recipeCode && x.Version == version, ct)
            ?? throw new InvalidOperationException("Recipe version does not exist");
        var package = new AssetPackage("vwrecipe", item.RecipeCode, item.Version, item.DefinitionJson, item.ContentSha256);
        return JsonSerializer.Serialize(package, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>导入始终创建新版本，不覆盖同名已有版本。</summary>
    public async Task<RecipeVersionEntity> ImportRecipeAsync(string packageJson, string actor, CancellationToken ct = default)
    {
        var package = JsonSerializer.Deserialize<AssetPackage>(packageJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Recipe package is invalid");
        if (!string.Equals(package.Type, "vwrecipe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported recipe package type");
        ValidateJson(package.Content);
        if (!string.Equals(Hash(package.Content), package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recipe package hash mismatch");
        return await SaveRecipeDraftAsync(package.Code, package.Content, actor, ct);
    }

    private sealed record AssetPackage(string Type, string Code, int Version, string Content, string Sha256);

    private async Task RequireAsync(string actor, string permission, CancellationToken ct)
    {
        if (access is not null) await access.RequirePermissionAsync(actor, permission, ct);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void ValidateJson(string value) { try { JsonDocument.Parse(value).Dispose(); } catch (JsonException ex) { throw new InvalidDataException("版本内容不是有效 JSON", ex); } }
}
