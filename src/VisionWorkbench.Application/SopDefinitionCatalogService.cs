using System.Text.Json;
using VisionWorkbench.Domain;

namespace VisionWorkbench.Application;

/// <summary>SOP 定义目录：独立保存可复用的流程模板，任务保存时仍固化定义快照。</summary>
public sealed class SopDefinitionCatalogService(string configDirectory)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path = Path.Combine(configDirectory, "sop-definitions.json");

    public async Task<IReadOnlyList<SopDefinition>> ListAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<SopDefinition>>(stream, Options, ct) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"SOP目录格式无效：{ex.Message}", ex);
        }
    }

    public async Task SaveAsync(SopDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var definitions = (await ListAsync(ct)).ToList();
        var index = definitions.FindIndex(item => string.Equals(item.Id, definition.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            definitions[index] = definition;
        }
        else
        {
            definitions.Add(definition);
        }

        var temporary = _path + ".new-" + Guid.NewGuid().ToString("N");
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, definitions, Options, ct);
        }
        File.Move(temporary, _path, overwrite: true);
    }

    public async Task DeleteAsync(string definitionId, CancellationToken ct = default)
    {
        var definitions = (await ListAsync(ct))
            .Where(item => !string.Equals(item.Id, definitionId, StringComparison.Ordinal))
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".new-" + Guid.NewGuid().ToString("N");
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, definitions, Options, ct);
        }
        File.Move(temporary, _path, overwrite: true);
    }
}
