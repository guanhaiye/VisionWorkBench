using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

public partial class CommunicationPage : UserControl
{
    private readonly TcpCommunicationProfileStore _profileStore;
    private ProjectCommunicationConfig _config = new();
    private List<ProjectCommunicationConfig> _profiles = [];
    private List<ProjectListRow> _projectListRows = [];
    private List<TaskEntity> _allTasks = [];
    private List<TaskEntity> _triggerTasks = [];
    private readonly HashSet<long> _pendingTriggerTaskIds = [];
    private sealed record TriggerRuleRow(long TaskId, string DisplayText);
    private sealed class ProjectListRow(ProjectCommunicationConfig profile) : INotifyPropertyChanged
    {
        private string _status = "未连接";
        public ProjectCommunicationConfig Profile { get; } = profile;
        public string Name => Profile.Name;
        public string Status
        {
            get => _status;
            set
            {
                if (string.Equals(_status, value, StringComparison.Ordinal)) return;
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
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
        RefreshProjectListItems();
        _allTasks = await AppServices.Instance.Tasks.ListAsync();
        var preferredCode = AppServices.Instance.Settings.LastTcpProjectCode;
        var preferred = _profiles.FirstOrDefault(profile =>
            string.Equals(profile.ProjectCode, preferredCode, StringComparison.OrdinalIgnoreCase));
        if (preferred is null)
        {
            var projectWithRule = _allTasks
                .SelectMany(task => ReadTriggerConfigs(task.TriggerJson).Keys)
                .FirstOrDefault(code => _profiles.Any(profile =>
                    string.Equals(profile.ProjectCode, code, StringComparison.OrdinalIgnoreCase)));
            preferred = _profiles.FirstOrDefault(profile =>
                string.Equals(profile.ProjectCode, projectWithRule, StringComparison.OrdinalIgnoreCase));
        }
        preferred ??= _profiles.FirstOrDefault();
        if (preferred is not null)
        {
            ProjectCombo.SelectedItem = preferred;
            SelectProjectListItem(preferred.ProjectCode);
            AppServices.Instance.Settings.LastTcpProjectCode = preferred.ProjectCode;
            AppServices.Instance.SaveUserSettings();
        }
        RefreshBoundTasks();
    }
    private void Project_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectCombo.SelectedItem is ProjectCommunicationConfig profile)
        {
            SelectProjectListItem(profile.ProjectCode);
            RememberSelectedProject(profile);
        }
        LoadConfig();
    }
    private void ProjectList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectListRow row) return;
        var profile = row.Profile;
        ProjectCombo.SelectedItem = profile;
        RememberSelectedProject(profile);
        LoadConfig();
    }
    private static void RememberSelectedProject(ProjectCommunicationConfig profile)
    {
        if (string.Equals(AppServices.Instance.Settings.LastTcpProjectCode, profile.ProjectCode, StringComparison.OrdinalIgnoreCase))
            return;
        AppServices.Instance.Settings.LastTcpProjectCode = profile.ProjectCode;
        AppServices.Instance.SaveUserSettings();
    }
    private void Load_Click(object sender, RoutedEventArgs e) => LoadConfig();
    private void LoadConfig()
    {
        var selected = ProjectList.SelectedItem as ProjectCommunicationConfig
            ?? (ProjectList.SelectedItem as ProjectListRow)?.Profile
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
        ListenAddressText.Text = _config.ListenAddress; ListenPortText.Text = _config.ListenPort.ToString(); RemoteAddressText.Text = _config.RemoteAddress; RemotePortText.Text = _config.RemotePort.ToString(); MaxConnectionsText.Text = _config.MaxConnections.ToString(); TerminatorText.Text = _config.MessageTerminator; MaxMessageText.Text = _config.MaxMessageBytes.ToString(); ReceiveTimeoutText.Text = _config.ReceiveTimeoutMs.ToString(); AutoReconnectCheck.IsChecked = _config.AutoReconnect; AutoStartCheck.IsChecked = _config.AutoStart; UpdateModeVisibility();
    }
    private void ReadControls()
    {
        _config.Name = string.IsNullOrWhiteSpace(ProjectNameText.Text) ? _config.ProjectCode : ProjectNameText.Text.Trim();
        _config.Enabled = EnabledCheck.IsChecked == true; _config.WorkMode = Enum.Parse<TcpWorkMode>((ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Disabled"); _config.FrameMode = Enum.Parse<TcpFrameMode>((FrameCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Line"); _config.Encoding = (EncodingCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "utf-8"; _config.ListenAddress = ListenAddressText.Text.Trim(); _config.ListenPort = ParsePort(ListenPortText.Text, 5000); _config.RemoteAddress = RemoteAddressText.Text.Trim(); _config.RemotePort = ParsePort(RemotePortText.Text, 5000); _config.MaxConnections = Math.Clamp(ParseInt(MaxConnectionsText.Text, 10), 1, 100); _config.MessageTerminator = TerminatorText.Text; _config.MaxMessageBytes = Math.Clamp(ParseInt(MaxMessageText.Text, 1024 * 1024), 1024, 16 * 1024 * 1024); _config.ReceiveTimeoutMs = Math.Clamp(ParseInt(ReceiveTimeoutText.Text, 30000), 1000, 300000); _config.AutoReconnect = AutoReconnectCheck.IsChecked == true; _config.AutoStart = AutoStartCheck.IsChecked == true;
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
        RefreshProjectListItems(profile.ProjectCode);
        ProjectCombo.SelectedItem = profile;
        SelectProjectListItem(profile.ProjectCode);
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
        RefreshProjectListItems();
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
        ProjectCombo.ItemsSource = _profiles;
        RefreshProjectListItems(selectedProjectCode);
        ProjectCombo.SelectedItem = selected;
        SelectProjectListItem(selectedProjectCode);
    }
    private void RefreshProjectListItems(string? selectedProjectCode = null)
    {
        var code = selectedProjectCode
            ?? (ProjectList.SelectedItem as ProjectListRow)?.Profile.ProjectCode;
        _projectListRows = _profiles
            .Select(profile => new ProjectListRow(profile)
            {
                Status = GetProjectStatus(profile),
            })
            .ToList();
        ProjectList.ItemsSource = _projectListRows;
        if (!string.IsNullOrWhiteSpace(code))
            SelectProjectListItem(code);
    }
    private void SelectProjectListItem(string projectCode)
    {
        ProjectList.SelectedItem = _projectListRows.FirstOrDefault(row =>
            string.Equals(row.Profile.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase));
    }
    private string GetProjectStatus(ProjectCommunicationConfig profile)
    {
        return AppServices.Instance.TcpCommunication.GetState(profile.ProjectCode) switch
        {
            TcpRuntimeState.Connected => "已连接",
            TcpRuntimeState.Listening => "监听中",
            TcpRuntimeState.Connecting => "连接中",
            TcpRuntimeState.Reconnecting => "重连中",
            TcpRuntimeState.Starting => "启动中",
            TcpRuntimeState.Stopping => "停止中",
            TcpRuntimeState.Faulted => "连接失败",
            _ => "未连接",
        };
    }
    private void RefreshProjectStatuses()
    {
        foreach (var row in _projectListRows)
            row.Status = GetProjectStatus(row.Profile);
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TrySaveProjectParameters(refreshSelectors: false)) return;
            RefreshProjectStatuses();
            await AppServices.Instance.TcpCommunication.StartAsync(_config);
            StateText.Text = !_config.Enabled || _config.WorkMode == TcpWorkMode.Disabled
                ? "TCP/IP 通讯未启用，请先勾选启用 TCP/IP 通讯并选择工作模式"
                : "已启动";
            RefreshProjectStatuses();
        }
        catch (Exception ex)
        {
            RefreshProjectStatuses();
            ThemedMessageBox.Show(ex.Message, "启动 TCP/IP 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await AppServices.Instance.TcpCommunication.StopProjectAsync(_config.ProjectCode);
        RefreshProjectStatuses();
        StateText.Text = "已停止";
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    { try { var connection = (ConnectionsList.SelectedItem as TcpConnectionInfo)?.ConnectionId ?? (AppServices.Instance.TcpCommunication.GetConnections(_config.ProjectCode).FirstOrDefault()?.ConnectionId ?? "client"); await AppServices.Instance.TcpCommunication.SendTextAsync(_config.ProjectCode, connection, ManualText.Text); } catch (Exception ex) { ThemedMessageBox.Show(ex.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning); } }
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
        // 检测任务下拉框只负责切换任务，不改变规则编辑器的启用状态。
        // 编辑器是否启用仅由规则列表是否被点击选中决定，规则名称也不参与匹配判断。
    }
    private void LoadTriggerEditor(TaskEntity task)
    {
        TaskTcpTriggerConfig config;
        config = TryReadTrigger(task.TriggerJson, _config.ProjectCode) ?? new TaskTcpTriggerConfig();
        TriggerEnabledCheck.IsChecked = config.Enabled;
        TaskNameText.Text = GetTriggerRuleName(task, config);
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
        LoadTriggerRule(row);
    }
    private void TriggerRulesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(TriggerRulesList, e.OriginalSource as DependencyObject)
            is ListBoxItem { DataContext: TriggerRuleRow row })
        {
            TriggerRulesList.SelectedItem = row;
            LoadTriggerRule(row);
        }
    }
    private void LoadTriggerRule(TriggerRuleRow row)
    {
        var task = _triggerTasks.FirstOrDefault(item => item.Id == row.TaskId);
        if (task is null) return;
        TriggerTaskCombo.SelectedItem = task;
        LoadTriggerEditor(task);
    }
    private async void NewTaskTrigger_Click(object sender, RoutedEventArgs e)
    {
        var configuredIds = TriggerRulesList.Items.OfType<TriggerRuleRow>()
            .Select(row => row.TaskId)
            .ToHashSet();
        var task = _allTasks.FirstOrDefault(item => !configuredIds.Contains(item.Id));
        if (task is null) { ThemedMessageBox.Show("所有检测任务都已配置规则", "TCP/IP 设置"); return; }
        _pendingTriggerTaskIds.Add(task.Id);
        var defaultConfig = new TaskTcpTriggerConfig
        {
            Enabled = true,
            TcpProjectCode = _config.ProjectCode,
            RuleName = GetDefaultTriggerRuleName(task),
            MatchMode = MessageMatchMode.ExactText,
            MatchValue = $"START_{task.StationCode}",
        };
        SetTriggerConfig(task, defaultConfig);
        try
        {
            await AppServices.Instance.Tasks.SaveAsync(task);
            _pendingTriggerTaskIds.Remove(task.Id);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(ex.Message, "保存任务规则失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshTriggerRules();
        TriggerRulesList.SelectedItem = null;
        TriggerTaskCombo.SelectedItem = task;
        TriggerRulesList.SelectedItem = TriggerRulesList.Items
            .OfType<TriggerRuleRow>()
            .FirstOrDefault(row => row.TaskId == task.Id);
        TriggerEnabledCheck.IsChecked = true;
        TriggerMatchModeCombo.SelectedIndex = 0;
        TriggerMatchValueText.Text = $"START_{task.StationCode}";
        TriggerResponseTemplateText.Text = defaultConfig.ResponseTemplate;
        TaskNameText.Text = defaultConfig.RuleName;
        SetTriggerEditorEnabled(true);
    }
    private async void DeleteTaskTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (TriggerRulesList.SelectedItem is not TriggerRuleRow row) return;
        var task = _triggerTasks.First(item => item.Id == row.TaskId);
        RemoveTriggerConfig(task, _config.ProjectCode);
        await AppServices.Instance.Tasks.SaveAsync(task);
        RefreshBoundTasks();
        StateText.Text = $"已删除任务规则：{task.Name}";
    }
    private async void SaveTaskTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveProjectParameters(refreshSelectors: false))
            return;
        if (TriggerRulesList.SelectedItem is not TriggerRuleRow row)
        {
            StateText.Text = "项目通讯及协议参数已保存";
            return;
        }
        var ruleTask = _triggerTasks.FirstOrDefault(item => item.Id == row.TaskId);
        var task = TriggerTaskCombo.SelectedItem as TaskEntity;
        if (ruleTask is null || task is null)
        {
            StateText.Text = "项目通讯及协议参数已保存";
            return;
        }
        var modeText = (TriggerMatchModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ExactText";
        var config = new TaskTcpTriggerConfig
        {
            Enabled = TriggerEnabledCheck.IsChecked == true,
            TcpProjectCode = _config.ProjectCode,
            RuleName = string.IsNullOrWhiteSpace(TaskNameText.Text)
                ? GetDefaultTriggerRuleName(task)
                : TaskNameText.Text.Trim(),
            MatchMode = Enum.Parse<MessageMatchMode>(modeText),
            MatchValue = TriggerMatchValueText.Text,
            ResponseTemplate = TriggerResponseTemplateText.Text,
        };
        if (config.Enabled && string.IsNullOrWhiteSpace(config.MatchValue))
        {
            ThemedMessageBox.Show("启用任务触发时，接收消息不能为空", "TCP/IP 设置");
            return;
        }
        if (task.Id != ruleTask.Id
            && TryReadTrigger(task.TriggerJson, _config.ProjectCode) is not null)
        {
            ThemedMessageBox.Show("所选检测任务已经存在当前 TCP/IP 项目的规则，请先删除原规则或选择其他任务", "TCP/IP 设置");
            return;
        }
        if (config.Enabled && _triggerTasks.Any(other => other.Id != task.Id && other.Id != ruleTask.Id
            && TryReadTrigger(other.TriggerJson, _config.ProjectCode) is { Enabled: true } existing
            && existing.MatchMode == config.MatchMode
            && string.Equals(existing.MatchValue, config.MatchValue, StringComparison.Ordinal)))
        {
            ThemedMessageBox.Show("该触发消息已被其他任务使用，请为每个任务配置唯一消息", "TCP/IP 设置");
            return;
        }
        try
        {
            SetTriggerConfig(task, config);
            await AppServices.Instance.Tasks.SaveAsync(task);
            if (task.Id != ruleTask.Id)
            {
                RemoveTriggerConfig(ruleTask, _config.ProjectCode);
                await AppServices.Instance.Tasks.SaveAsync(ruleTask);
            }
            _allTasks = await AppServices.Instance.Tasks.ListAsync();
            RefreshBoundTasks();
            var savedRow = TriggerRulesList.Items.OfType<TriggerRuleRow>().FirstOrDefault(item => item.TaskId == task.Id);
            if (savedRow is not null)
            {
                TriggerRulesList.SelectedItem = savedRow;
                LoadTriggerRule(savedRow);
            }
            StateText.Text = $"已保存任务规则：{config.RuleName}";
        }
        catch (Exception ex)
        {
            SetTriggerEditorEnabled(true);
            ThemedMessageBox.Show(ex.Message, "保存任务规则失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void RefreshTriggerRules()
    {
        TriggerRulesList.ItemsSource = _triggerTasks
            .Select(task => (Task: task, Config: TryReadTrigger(task.TriggerJson, _config.ProjectCode)))
            .Where(pair => _pendingTriggerTaskIds.Contains(pair.Task.Id)
                || (pair.Config is { }
                    && string.Equals(pair.Config.TcpProjectCode, _config.ProjectCode, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => new TriggerRuleRow(pair.Task.Id,
                $"{GetTriggerRuleName(pair.Task, pair.Config)}  ·  {pair.Config?.MatchMode ?? MessageMatchMode.ExactText}  ·  {pair.Config?.MatchValue ?? "待保存"}"))
            .ToArray();
    }
    private string GetTriggerRuleName(TaskEntity task, TaskTcpTriggerConfig? config)
    {
        return string.IsNullOrWhiteSpace(config?.RuleName)
            ? GetDefaultTriggerRuleName(task)
            : config.RuleName.Trim();
    }
    private string GetDefaultTriggerRuleName(TaskEntity task)
    {
        return string.IsNullOrWhiteSpace(_config.Name) ? task.Name : _config.Name.Trim();
    }
    private static TaskTcpTriggerConfig? TryReadTrigger(string? json, string? projectCode = null)
    {
        var configs = ReadTriggerConfigs(json);
        if (projectCode is not null && configs.TryGetValue(projectCode, out var config))
            return config;
        return projectCode is null ? configs.Values.FirstOrDefault() : null;
    }
    private static Dictionary<string, TaskTcpTriggerConfig> ReadTriggerConfigs(string? json)
    {
        var configs = new Dictionary<string, TaskTcpTriggerConfig>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return configs;
        try
        {
            using var document = JsonDocument.Parse(json);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return configs;

            if (document.RootElement.TryGetProperty("TcpProjectCode", out _)
                || document.RootElement.TryGetProperty("tcpProjectCode", out _))
            {
                var single = JsonSerializer.Deserialize<TaskTcpTriggerConfig>(json, options);
                if (single is not null && !string.IsNullOrWhiteSpace(single.TcpProjectCode))
                    configs[single.TcpProjectCode] = single;
                return configs;
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, TaskTcpTriggerConfig>>(json, options);
            if (map is null) return configs;
            foreach (var pair in map)
            {
                if (pair.Value is null) continue;
                pair.Value.TcpProjectCode = string.IsNullOrWhiteSpace(pair.Value.TcpProjectCode)
                    ? pair.Key
                    : pair.Value.TcpProjectCode;
                configs[pair.Key] = pair.Value;
            }
        }
        catch (JsonException) { }
        return configs;
    }
    private void SetTriggerConfig(TaskEntity task, TaskTcpTriggerConfig config)
    {
        config.TcpProjectCode = _config.ProjectCode;
        var configs = ReadTriggerConfigs(task.TriggerJson);
        configs[_config.ProjectCode] = config;
        task.TriggerJson = JsonSerializer.Serialize(configs, new JsonSerializerOptions { WriteIndented = true });
    }
    private static void RemoveTriggerConfig(TaskEntity task, string projectCode)
    {
        var configs = ReadTriggerConfigs(task.TriggerJson);
        configs.Remove(projectCode);
        task.TriggerJson = configs.Count == 0
            ? null
            : JsonSerializer.Serialize(configs, new JsonSerializerOptions { WriteIndented = true });
    }
    private void SetTriggerEditorEnabled(bool enabled)
    {
        TaskNameText.IsEnabled = enabled;
        TriggerEnabledCheck.IsEnabled = enabled;
        TriggerMatchModeCombo.IsEnabled = enabled;
        TriggerMatchValueText.IsEnabled = enabled;
        TriggerResponseTemplateText.IsEnabled = enabled;
    }
    private void UpdateModeVisibility() { var tag = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(); var server = tag == "Server"; ListenAddressText.IsEnabled = server; ListenPortText.IsEnabled = server; MaxConnectionsText.IsEnabled = server; RemoteAddressText.IsEnabled = tag == "Client"; RemotePortText.IsEnabled = tag == "Client"; }
    private void Tcp_LogReceived(object? sender, TcpLogEntry e) => Dispatcher.Invoke(() => { LogText.AppendText($"[{e.Timestamp:HH:mm:ss}] {e.Level} {e.Direction} {e.Message}{Environment.NewLine}"); LogText.ScrollToEnd(); ConnectionsList.ItemsSource = AppServices.Instance.TcpCommunication.GetConnections(_config.ProjectCode).ToArray(); });
    private void Tcp_StateChanged(object? sender, EventArgs e) => Dispatcher.Invoke(() =>
    {
        StateText.Text = AppServices.Instance.TcpCommunication.GetState(_config.ProjectCode).ToString();
        ConnectionsList.ItemsSource = AppServices.Instance.TcpCommunication.GetConnections(_config.ProjectCode).ToArray();
        RefreshProjectStatuses();
    });
    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var value) ? value : fallback;
    private static int ParsePort(string text, int fallback) => Math.Clamp(ParseInt(text, fallback), 1, 65535);
    private static void SelectTag(ComboBox box, string tag) { box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)); }
    private static void SelectText(ComboBox box, string text) { box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase)) ?? box.Items[0]; }
}
