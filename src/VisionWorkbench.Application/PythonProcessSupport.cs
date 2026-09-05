using System.Diagnostics;
using System.Text.Json;

namespace VisionWorkbench.Application;

internal static class PythonProcessSupport
{
    public static string ResolvePython(string? configured, params string[] environmentRoots)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return environmentRoots
            .Select(root => Path.Combine(root, ".venv", "Scripts", "python.exe"))
            .FirstOrDefault(File.Exists) ?? "python";
    }

    public static void TryKill(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or NotSupportedException)
        {
            // Best-effort cleanup: the process may already have exited or been disposed.
        }
    }

    public static bool TryParseEvent(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("event", out _);
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
