using System.Windows;
using System.Windows.Controls;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

/// <summary>项目/工位管理页：稳定编号、项目内唯一校验、历史工位归档。</summary>
public partial class ProjectStationsPage : UserControl
{
    private long? _projectId;
    private long? _stationId;

    public ProjectStationsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var projects = await AppServices.Instance.Projects.ListProjectsAsync();
            ProjectList.ItemsSource = projects;
            if (_projectId is { } id)
            {
                ProjectList.SelectedItem = projects.FirstOrDefault(p => p.Id == id);
            }
            if (ProjectList.SelectedItem is null && projects.Count > 0)
            {
                ProjectList.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            HintText.Text = $"刷新失败：{ex.Message}";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        _projectId = null;
        ProjectList.SelectedItem = null;
        ProjectCodeText.Text = $"project-{DateTime.UtcNow:MMddHHmmss}";
        ProjectNameText.Text = "新项目";
        ProjectDescriptionText.Text = "";
        StationList.ItemsSource = null;
    }

    private async void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectEntity project)
        {
            return;
        }
        _projectId = project.Id;
        ProjectCodeText.Text = project.ProjectCode;
        ProjectNameText.Text = project.Name;
        ProjectDescriptionText.Text = project.Description ?? "";
        StationList.ItemsSource = await AppServices.Instance.Projects.ListStationsAsync(project.Id);
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var project = await AppServices.Instance.Projects.SaveProjectAsync(new ProjectEntity
            {
                Id = _projectId ?? 0,
                ProjectCode = ProjectCodeText.Text,
                Name = ProjectNameText.Text.Trim(),
                Description = ProjectDescriptionText.Text,
            });
            _projectId = project.Id;
            HintText.Text = "项目已保存";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            HintText.Text = $"项目保存失败：{ex.Message}";
        }
    }

    private void NewStation_Click(object sender, RoutedEventArgs e)
    {
        if (_projectId is not { } projectId)
        {
            HintText.Text = "请先选择或保存项目";
            return;
        }
        _stationId = null;
        var used = StationList.Items.OfType<StationEntity>().Select(s => s.StationCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = 1;
        while (used.Contains($"ST-{number:000}")) number++;
        StationCodeText.Text = $"ST-{number:000}";
        StationNameText.Text = "新工位";
        StationEnabledCheck.IsChecked = true;
        StationTaskIdText.Text = "";
    }

    private void StationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StationList.SelectedItem is not StationEntity station)
        {
            return;
        }
        _stationId = station.Id;
        StationCodeText.Text = station.StationCode;
        StationNameText.Text = station.Name;
        StationEnabledCheck.IsChecked = station.Enabled;
        StationTaskIdText.Text = station.TaskId?.ToString() ?? "";
    }

    private async void SaveStation_Click(object sender, RoutedEventArgs e)
    {
        if (_projectId is not { } projectId)
        {
            HintText.Text = "请先选择项目";
            return;
        }
        if (!string.IsNullOrWhiteSpace(StationTaskIdText.Text)
            && !long.TryParse(StationTaskIdText.Text, out _))
        {
            HintText.Text = "任务 ID 必须是数字";
            return;
        }
        long? parsedTaskId = long.TryParse(StationTaskIdText.Text, out var parsed) ? parsed : null;
        try
        {
            await AppServices.Instance.Projects.SaveStationAsync(new StationEntity
            {
                Id = _stationId ?? 0,
                ProjectId = projectId,
                StationCode = StationCodeText.Text,
                Name = StationNameText.Text.Trim(),
                Enabled = StationEnabledCheck.IsChecked == true,
                TaskId = parsedTaskId,
            });
            HintText.Text = "工位已保存";
            StationList.ItemsSource = await AppServices.Instance.Projects.ListStationsAsync(projectId);
        }
        catch (Exception ex)
        {
            HintText.Text = $"工位保存失败：{ex.Message}";
        }
    }

    private async void ArchiveStation_Click(object sender, RoutedEventArgs e)
    {
        if (StationList.SelectedItem is not StationEntity station)
        {
            return;
        }
        if (MessageBox.Show($"归档/删除工位「{station.StationCode}」？", "确认", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        try
        {
            await AppServices.Instance.Projects.ArchiveOrDeleteStationAsync(station.Id);
            HintText.Text = "工位已处理；有历史记录的工位会保留为归档状态";
            if (_projectId is { } projectId)
            {
                StationList.ItemsSource = await AppServices.Instance.Projects.ListStationsAsync(projectId);
            }
        }
        catch (Exception ex)
        {
            HintText.Text = $"工位处理失败：{ex.Message}";
        }
    }
}
