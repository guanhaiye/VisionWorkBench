using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

public partial class CommunicationPage : UserControl
{
    private readonly TcpCommunicationProfileStore _profileStore;
    private ProjectCommunicationConfig _config = new();
    private List<ProjectCommunicationConfig> _profiles = [];
    private List<TaskEntity> _allTasks = [];
    private List<TaskEntity> _triggerTasks = [];
    private readonly HashSet<long> _pendingTriggerTaskIds = [];
    private bool _loadingTriggerEditor;
    private sealed record TriggerRuleRow(long TaskId, string DisplayText);
    public CommunicationPage()
    {
        InitializeComponent();
        SetTriggerEditorEnabled(false);
        MoveStateToBottomRight();
        ManualText.Text = "{\"command\":\"ping\",\"requestId\":\"PING-001\"}";
        _profileStore = new TcpCommunicationProfileStore(AppServices.Instance.Settings.ConfigDirectory);
        AppServices.Instance.TcpCommunication.LogReceived += Tcp_LogReceived;
        AppServices.Instance.TcpCommunication.StateChanged += Tcp_StateChanged;
        Loaded += CommunicationPage_Loaded;
        Unloaded += CommunicationPage_Unloaded;
    }

    private async void CommunicationPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
            await LoadProjectsAsync();
    }

    private void CommunicationPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count > 0)
            TrySaveProjectParameters(refreshSelectors: false);
        AppServices.Instance.TcpCommunication.LogReceived -= Tcp_LogReceived;
        AppServices.Instance.TcpCommunication.StateChanged -= Tcp_StateChanged;
    }

    private void MoveStateToBottomRight()
    {
        if (StateText.Parent is Panel parent) parent.Children.Remove(StateText);
        Grid.SetRow(StateText, 4);
        Grid.SetColumn(StateText, 0);
        Grid.SetColumnSpan(StateText, 3);
        StateText.HorizontalAlignment = HorizontalAlignment.Right;
        StateText.TextAlignment = TextAlignment.Right;
        StateText.TextWrapping = TextWrapping.Wrap;
        StateText.MaxWidth = 620;
        StateText.Margin = new Thickness(0, 0, 0, 2);
        RightContentGrid.Children.Add(StateText);
    }
    private async Task LoadProjectsAsync()
    {
        _profiles = _profileStore.Load();
        ProjectCombo.ItemsSource = _profiles;
        ProjectList.ItemsSource = _profiles;
        if (_profiles.Count > 0) ProjectCombo.SelectedIndex = 0;
        if (_profiles.Count > 0) ProjectList.SelectedIndex = 0;
        _allTasks = await AppServices.Instance.Tasks.ListAsync();
        RefreshBoundTasks();
    }
    private void Project_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectCombo.SelectedItem is ProjectCommunicationConfig profile)
            ProjectList.SelectedItem = profile;
        LoadConfig();
    }
    private void ProjectList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectCommunicationConfig profile) return;
        ProjectCombo.SelectedItem = profile;
        LoadConfig();
    }
    private void Load_Click(object sender, RoutedEventArgs e) => LoadConfig();
    private void LoadConfig()
    {
        var selected = ProjectList.SelectedItem as ProjectCommunicationConfig
            ?? ProjectCombo.SelectedItem as ProjectCommunicationConfig;
        var code = selected?.ProjectCode ?? "default";
        _config = _profiles.FirstOrDefault(profile => string.Equals(profile.ProjectCode, code, StringComparison.OrdinalIgnoreCase))
            ?? TcpCommunicationProfileStore.CreateDefault();
        _config.ProjectCode = code;
        FillControls();
        RefreshBoundTasks();
    }

    private void RefreshBoundTasks()
    {
        _pendingTriggerTaskIds.Clear();
        _triggerTasks = _allTasks.ToList();
        TriggerTaskCombo.ItemsSource = _triggerTasks;
        TriggerTaskCombo.SelectedIndex = -1;
        RefreshTriggerRules();
        SetTriggerEditorEnabled(false);
    }
    private void FillControls()
    {
        ProjectNameText.Text = _config.Name;
        EnabledCheck.IsChecked = _config.Enabled; SelectTag(ModeCombo, _config.WorkMode.ToString()); SelectTag(FrameCombo, _config.FrameMode.ToString()); SelectText(EncodingCombo, _config.Encoding);
        ListenAddressText.Text = _config.ListenAddress; ListenPortText.Text = _config.ListenPort.ToString(); RemoteAddressText.Text = _config.RemoteAddress; RemotePortText.Text = _config.RemotePort.ToString(); MaxConnectionsText.Text = _config.MaxConnections.ToString(); TerminatorText.Text = _config.MessageTerminator; MaxMessageText.Text = _config.MaxMessageBytes.ToString(); ReceiveTimeoutText.Text = _config.ReceiveTimeoutMs.ToString(); AutoReconnectCheck.IsChecked = _config.AutoReconnect; UpdateModeVisibility();
    }
    private void ReadControls()
    {
        _config.Name = string.IsNullOrWhiteSpace(ProjectNameText.Text) ? _config.ProjectCode : ProjectNameText.Text.Trim();
        _config.Enabled = EnabledCheck.IsChecked == true; _config.WorkMode = Enum.Parse<TcpWorkMode>((ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Disabled"); _config.FrameMode = Enum.Parse<TcpFrameMode>((FrameCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Line"); _config.Encoding = (EncodingCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "utf-8"; _config.ListenAddress = ListenAddressText.Text.Trim(); _config.ListenPort = ParsePort(ListenPortText.Text, 5000); _config.RemoteAddress = RemoteAddressText.Text.Trim(); _config.RemotePort = ParsePort(RemotePortText.Text, 5000); _config.MaxConnections = Math.Clamp(ParseInt(MaxConnectionsText.Text, 10), 1, 100); _config.MessageTerminator = TerminatorText.Text; _config.MaxMessageBytes = Math.Clamp(ParseInt(MaxMessageText.Text, 1024 * 1024), 1024, 16 * 1024 * 1024); _config.ReceiveTimeoutMs = Math.Clamp(ParseInt(ReceiveTimeoutText.Text, 30000), 1000, 300000); _config.AutoReconnect = AutoReconnectCheck.IsChecked == true;
    }
    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var number = 1;
        string code;
        do { code = $"tcp-{number++:000}"; } while (_profiles.Any(profile => string.Equals(profile.ProjectCode, code, StringComparison.OrdinalIgnoreCase)));
        var profile = new ProjectCommunicationConfig { ProjectCode = code, Name = $"TCP/IP 项目 {number - 1}" };
        _profiles.Add(profile);
        ProjectCombo.ItemsSource = null;
        ProjectCombo.ItemsSource = _profiles;
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _profiles;
        ProjectCombo.SelectedItem = profile;
        ProjectList.SelectedItem = profile;
        _profileStore.Save(_profiles);
        StateText.Text = $"已创建 TCP/IP 项目：{profile.Name}";
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectCombo.SelectedItem is not ProjectCommunicationConfig profile) return;
        if (_profiles.Count <= 1)
        {
            ThemedMessageBox.Show("至少保留一个 TCP/IP 项目。", "TCP/IP 项目");
            return;
        }
        if (ThemedMessageBox.Show($"删除 TCP/IP 项目“{profile.Name}”？", "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (string.Equals(_config.ProjectCode, profile.ProjectCode, StringComparison.OrdinalIgnoreCase))
            _ = AppServices.Instance.TcpCommunication.StopAsync();
        _profiles.Remove(profile);
        _profileStore.Save(_profiles);
        ProjectCombo.ItemsSource = null;
        ProjectCombo.ItemsSource = _profiles;
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _profiles;
        ProjectCombo.SelectedIndex = 0;
        ProjectList.SelectedIndex = 0;
        StateText.Text = $"已删除 TCP/IP 项目：{profile.Name}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TrySaveProjectParameters())
            StateText.Text = "配置已保存";
    }

    private bool TrySaveProjectParameters(bool refreshSelectors = true)
    {
        try
        {
            ReadControls();
            var index = _profiles.FindIndex(profile =>
                string.Equals(profile.ProjectCode, _config.ProjectCode, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                _profiles[index] = _config;
            else
                _profiles.Add(_config);
            _profileStore.Save(_profiles);
            if (refreshSelectors)
                RefreshProjectSelectors(_config.ProjectCode);
            return true;
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(ex.Message, "保存通讯配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RefreshProjectSelectors(string selectedProjectCode)
    {
        var selected = _profiles.FirstOrDefault(profile =>
            string.Equals(profile.ProjectCode, selectedProjectCode, StringComparison.OrdinalIgnoreCase));
        ProjectCombo.ItemsSource = null;
        ProjectList.ItemsSource = null;
        ProjectCombo.ItemsSource = _profiles;
        ProjectList.ItemsSource = _profiles;
        ProjectCombo.SelectedItem = selected;
        ProjectList.SelectedItem = selected;
    }
    private async void Start_Click(object sender, RoutedEventArgs e) { try { if (!TrySaveProjectParameters(refreshSelectors: false)) return; await AppServices.Instance.TcpCommunication.StartAsync(_config); StateText.Text = !_config.Enabled || _config.WorkMode == TcpWorkMode.Disabled ? "TCP/IP 通讯未启用，请先勾选启用 TCP/IP 通讯并选择工作模式" : "已启动"; } catch (Exception ex) { ThemedMessageBox.Show(ex.Message, "启动 TCP/IP 失败", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private async void Stop_Click(object sender, RoutedEventArgs e) { await AppServices.Instance.TcpCommunication.StopAsync(); StateText.Text = "已停止"; }
    private async void Send_Click(object sender, RoutedEventArgs e)
    { try { var connection = (ConnectionsList.SelectedItem as TcpConnectionInfo)?.ConnectionId ?? (AppServices.Instance.TcpCommunication.Connections.FirstOrDefault()?.ConnectionId ?? "client"); await AppServices.Instance.TcpCommunication.SendTextAsync(connection, ManualText.Text); } catch (Exception ex) { ThemedMessageBox.Show(ex.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    private void CommunicationSettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer outer || e.Delta == 0)
            return;

        var inner = FindNearestScrollViewer(e.OriginalSource as DependencyObject);
        if (inner is not null && !ReferenceEquals(inner, outer) && inner.ScrollableHeight > 0)
        {
            var canScrollInner = e.Delta > 0
                ? inner.VerticalOffset > 0
                : inner.VerticalOffset < inner.ScrollableHeight;
            if (canScrollInner)
            {
                inner.ScrollToVerticalOffset(Math.Clamp(
                    inner.VerticalOffset - e.Delta * 0.75,
                    0,
                    inner.ScrollableHeight));
                e.Handled = true;
                return;
            }
        }

        if (outer.ScrollableHeight <= 0)
            return;

        outer.ScrollToVerticalOffset(Math.Clamp(
            outer.VerticalOffset - e.Delta * 0.75,
            0,
            outer.ScrollableHeight));
        e.Handled = true;
    }
    private static ScrollViewer? FindNearestScrollViewer(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollViewer scrollViewer)
                return scrollViewer;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e) => UpdateModeVisibility();
    private void TriggerTask_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTriggerEditor) return;
        if (TriggerTaskCombo.SelectedItem is not TaskEntity task) return;
        var row = TriggerRulesList.Items.OfType<TriggerRuleRow>().FirstOrDefault(item => item.TaskId == task.Id);
        if (row is not null)
        {
            TriggerRulesList.SelectedItem = row;
            LoadTriggerEditor(task);
        }
        else
        {
            SetTriggerEditorEnabled(false);
        }
    }
    private void LoadTriggerEditor(TaskEntity task)
    {
        TaskTcpTriggerConfig config;
        try { config = JsonSerializer.Deserialize<TaskTcpTriggerConfig>(task.TriggerJson ?? "") ?? new TaskTcpTriggerConfig(); }
        catch (JsonException) { config = new TaskTcpTriggerConfig(); }
        if (!string.Equals(config.TcpProjectCode, _config.ProjectCode, StringComparison.OrdinalIgnoreCase))
        {
            config = new TaskTcpTriggerConfig();
        }
        TriggerEnabledCheck.IsChecked = config.Enabled;
        TaskNameText.Text = task.Name;
        SelectTag(TriggerMatchModeCombo, config.MatchMode.ToString());
        TriggerMatchValueText.Text = config.MatchValue;
        TriggerResponseTemplateText.Text = config.ResponseTemplate;
        SetTriggerEditorEnabled(true);
    }
    private void TriggerRule_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TriggerRulesList.SelectedItem is not TriggerRuleRow row)
        {
            SetTriggerEditorEnabled(false);
            return;
        }
        var task = _triggerTasks.FirstOrDefault(item => item.Id == row.TaskId);
        if (task is null) return;
        _loadingTriggerEditor = true;
        TriggerTaskCombo.SelectedItem = task;
        _loadingTriggerEditor = false;
        LoadTriggerEditor(task);
    }
    private void NewTaskTrigger_Click(object sender, RoutedEventArgs e)
    {
        var configuredIds = TriggerRulesList.Items.OfType<TriggerRuleRow>()
            .Select(row => row.TaskId)
            .ToHashSet();
        var task = _allTasks.FirstOrDefault(item => !configuredIds.Contains(item.Id));
        if (task is null) { ThemedMessageBox.Show("所有检测任务都已配置规则", "TCP/IP 设置"); return; }
        _pendingTriggerTaskIds.Add(task.Id);
        RefreshTriggerRules();
        TriggerRulesList.SelectedItem = null;
        _loadingTriggerEditor = true;
        TriggerTaskCombo.SelectedItem = task;
        _loadingTriggerEditor = false;
        TriggerRulesList.SelectedItem = TriggerRulesList.Items
            .OfType<TriggerRuleRow>()
            .FirstOrDefault(row => row.TaskId == task.Id);
        TriggerEnabledCheck.IsChecked = true;
        TriggerMatchModeCombo.SelectedIndex = 0;
        TriggerMatchValueText.Text = $"START_{task.StationCode}";
        TriggerResponseTemplateText.Text = new TaskTcpTriggerConfig().ResponseTemplate;
        TaskNameText.Text = task.Name;
        SetTriggerEditorEnabled(true);
    }
    private async void DeleteTaskTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (TriggerRulesList.SelectedItem is not TriggerRuleRow row) return;
        var task = _triggerTasks.First(item => item.Id == row.TaskId);
        task.TriggerJson = null;
        await AppServices.Instance.Tasks.SaveAsync(task);
        RefreshBoundTasks();
        StateText.Text = $"已删除任务规则：{task.Name}";
    }
    private async void SaveTaskTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveProjectParameters(refreshSelectors: false))
            return;
        if (TriggerTaskCombo.SelectedItem is not TaskEntity task)
        {
            return;
        }
        var modeText = (TriggerMatchModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ExactText";
        var config = new TaskTcpTriggerConfig
        {
            Enabled = TriggerEnabledCheck.IsChecked == true,
            TcpProjectCode = _config.ProjectCode,
            MatchMode = Enum.Parse<MessageMatchMode>(modeText),
            MatchValue = TriggerMatchValueText.Text,
            ResponseTemplate = TriggerResponseTemplateText.Text,
        };
        if (config.Enabled && string.IsNullOrWhiteSpace(config.MatchValue))
        {
            ThemedMessageBox.Show("启用任务触发时，接收消息不能为空", "TCP/IP 设置");
            return;
        }
        if (config.Enabled && _triggerTasks.Any(other => other.Id != task.Id
            && TryReadTrigger(other.TriggerJson) is { Enabled: true } existing
            && string.Equals(existing.TcpProjectCode, _config.ProjectCode, StringComparison.OrdinalIgnoreCase)
            && existing.MatchMode == config.MatchMode
            && string.Equals(existing.MatchValue, config.MatchValue, StringComparison.Ordinal)))
        {
            ThemedMessageBox.Show("该触发消息已被其他任务使用，请为每个任务配置唯一消息", "TCP/IP 设置");
            return;
        }
        task.Name = string.IsNullOrWhiteSpace(TaskNameText.Text) ? task.Name : TaskNameText.Text.Trim();
        task.TriggerJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await AppServices.Instance.Tasks.SaveAsync(task);
        _allTasks = await AppServices.Instance.Tasks.ListAsync();
        RefreshBoundTasks();
        var savedRow = TriggerRulesList.Items.OfType<TriggerRuleRow>().FirstOrDefault(row => row.TaskId == task.Id);
        if (savedRow is not null) TriggerRulesList.SelectedItem = savedRow;
        StateText.Text = $"已保存任务触发：{task.Name}";
    }
    private void RefreshTriggerRules()
    {
        TriggerRulesList.ItemsSource = _triggerTasks
            .Select(task => (Task: task, Config: TryReadTrigger(task.TriggerJson)))
            .Where(pair => _pendingTriggerTaskIds.Contains(pair.Task.Id)
                || (pair.Config is { }
                    && string.Equals(pair.Config.TcpProjectCode, _config.ProjectCode, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => new TriggerRuleRow(pair.Task.Id,
                $"{pair.Task.Name}  ·  {pair.Config?.MatchMode ?? MessageMatchMode.ExactText}  ·  {pair.Config?.MatchValue ?? "待保存"}"))
            .ToArray();
    }
    private static TaskTcpTriggerConfig? TryReadTrigger(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TaskTcpTriggerConfig>(json); }
        catch (JsonException) { return null; }
    }
    private void SetTriggerEditorEnabled(bool enabled)
    {
        TaskNameText.IsEnabled = enabled;
        TriggerEnabledCheck.IsEnabled = enabled;
        TriggerMatchModeCombo.IsEnabled = enabled;
        TriggerMatchValueText.IsEnabled = enabled;
        TriggerResponseTemplateText.IsEnabled = enabled;
        SaveTaskTriggerButton.IsEnabled = enabled;
    }
    private void UpdateModeVisibility() { var tag = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(); var server = tag == "Server"; ListenAddressText.IsEnabled = server; ListenPortText.IsEnabled = server; MaxConnectionsText.IsEnabled = server; RemoteAddressText.IsEnabled = tag == "Client"; RemotePortText.IsEnabled = tag == "Client"; }
    private void Tcp_LogReceived(object? sender, TcpLogEntry e) => Dispatcher.Invoke(() => { LogText.AppendText($"[{e.Timestamp:HH:mm:ss}] {e.Level} {e.Direction} {e.Message}{Environment.NewLine}"); LogText.ScrollToEnd(); ConnectionsList.ItemsSource = AppServices.Instance.TcpCommunication.Connections.ToArray(); });
    private void Tcp_StateChanged(object? sender, EventArgs e) => Dispatcher.Invoke(() => { StateText.Text = AppServices.Instance.TcpCommunication.State.ToString(); ConnectionsList.ItemsSource = AppServices.Instance.TcpCommunication.Connections.ToArray(); });
    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var value) ? value : fallback;
    private static int ParsePort(string text, int fallback) => Math.Clamp(ParseInt(text, fallback), 1, 65535);
    private static void SelectTag(ComboBox box, string tag) { box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)); }
    private static void SelectText(ComboBox box, string text) { box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase)) ?? box.Items[0]; }
}
