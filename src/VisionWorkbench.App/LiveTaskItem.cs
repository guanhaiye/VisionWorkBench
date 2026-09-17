namespace VisionWorkbench.App;

public sealed record LiveTaskItem(long Id, string StationCode, string TaskName)
{
    public string Number { get; } = $"任务 {Id}";

    public string Name { get; } = $"任务 {Id} · [{StationCode}] {TaskName}";

    public override string ToString() => Name;
}
