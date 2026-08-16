namespace VisionWorkbench.Tests;

/// <summary>测试辅助：从输出目录向上定位仓库根（含 src/workers 目录）。</summary>
public static class TestPaths
{
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    public static string Root => RepoRoot.Value;

    public static string VenvPython => Path.Combine(Root, "workers", ".venv", "Scripts", "python.exe");

    public static string SampleCounterPlugin => Path.Combine(Root, "workers", "sample-counter");

    public static string SamplesStaticCount => Path.Combine(Root, "samples", "static-count");

    public static bool VenvExists => File.Exists(VenvPython);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "workers")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到仓库根目录（tests 必须在仓库内运行）");
    }
}
