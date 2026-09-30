using System.Text.Json;
using VisionWorkbench.Application.Communication;

namespace VisionWorkbench.App;

/// <summary>Chooses how many TCP model slots to prepare from the projects that can trigger a task.</summary>
public static class TcpModelSessionPrewarmPolicy
{
    public static int GetConcurrency(
        string? triggerJson, IReadOnlyCollection<ProjectCommunicationConfig> profiles)
    {
        if (string.IsNullOrWhiteSpace(triggerJson)) return 1;
        try
        {
            using var document = JsonDocument.Parse(triggerJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return 1;
            var matchingConcurrency = new List<int>();
            foreach (var rule in document.RootElement.EnumerateArray())
            {
                if (!TryGetPropertyIgnoreCase(rule, "Enabled", out var enabled)
                    || enabled.ValueKind != JsonValueKind.True)
                    continue;
                var projectCode = TryGetPropertyIgnoreCase(rule, "TcpProjectCode", out var project)
                    && project.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(project.GetString())
                    ? project.GetString()!.Trim()
                    : "default";
                var profile = profiles.FirstOrDefault(item =>
                    string.Equals(item.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase));
                if (profile is not null)
                    matchingConcurrency.Add(Math.Clamp(profile.StationMaxConcurrency, 1, 8));
            }
            return matchingConcurrency.Count == 0 ? 1 : matchingConcurrency.Max();
        }
        catch (JsonException)
        {
            return 1;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
