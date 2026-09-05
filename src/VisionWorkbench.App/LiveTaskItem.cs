namespace VisionWorkbench.App;

public sealed record LiveTaskItem(long Id, string StationCode, string TaskName)
{
    public string Name { get; } = $"[{StationCode}] {TaskName}";

    public override string ToString() => Name;
}
