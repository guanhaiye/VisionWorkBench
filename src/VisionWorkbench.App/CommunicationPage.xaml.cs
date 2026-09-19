using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VisionWorkbench.Application.Communication;
using VisionWorkbench.Persistence;

namespace VisionWorkbench.App;

public partial class CommunicationPage : UserControl
{
    private readonly TcpCommunicationProfileStore _profileStore;
    private readonly string _configFile;
    private ProjectCommunicationConfig _config = new();
    private List<ProjectCommunicationConfig> _profiles = [];
    private List<TaskEntity> _allTasks = [];
    private List<TaskEntity> _triggerTasks = [];
    private bool _loadingTriggerEditor;
    private sealed record TriggerRuleRow(long TaskId, string DisplayText);
    public CommunicationPage()
    {
        InitializeComponent();
        MoveStateToBottomRight();
        ManualText.Text = "{\"command\":\"ping\",\"requestId\":\"PING-001\"}";
        _profileStore = new TcpCommunicationProfileStore(AppServices.Instance.Settings.ConfigDirectory);
        _configFile = _profileStore.FilePath;
        AppServices.Instance.TcpCommunication.LogReceived += Tcp_LogReceived;
        AppServices.Instance.TcpCommunication.StateChanged += Tcp_StateChanged;
        Loaded += async (_, _) => await LoadProjectsAsync();
        Unloaded += (_, _) => { AppServices.Instance.TcpCommunication.LogReceived -= Tcp_LogReceived; AppServices.Instance.TcpCommunication.StateChanged -= Tcp_StateChanged; };
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
        if (_profiles.Count > 0) ProjectCombo.SelectedIndex = 0;
        _allTasks = await AppServices.Instance.Tasks.ListAsync();
        RefreshBoundTasks();
    }
    private void Project_Changed(object sender, SelectionChangedEventArgs e) => LoadConfig();
    private void Load_Click(object sender, RoutedEventArgs e) => LoadConfig();
    private void LoadConfig()
    {
        var selected = ProjectCombo.SelectedItem as ProjectCommunicationConfig;
        var code = selected?.ProjectCode ?? "default";
        _config = _profiles.FirstOrDefault(profile => string.Equals(profile.ProjectCode, code, StringComparison.OrdinalIgnoreCase))
            ?? TcpCommunicationProfileStore.CreateDefault();
        _config.ProjectCode = code;
        FillControls();
        RefreshBoundTasks();
    }

    private void RefreshBoundTasks()
    {
        _triggerTasks = _allTasks
            .Where(task => TryReadTrigger(task.TriggerJson) is { } trigger
                && !string.IsNullOrWhiteSpace(trigger.TcpProjectCode)
                && string.Equals(trigger.TcpProjectCode, _config.ProjectCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        TriggerTaskCombo.ItemsSource = _triggerTasks;
        TriggerTaskCombo.SelectedIndex = _triggerTasks.Count > 0 ? 0 : -1;
        RefreshTriggerRules();
    }
    private void FillControls()
    {
        EnabledCheck.IsChecked = _config.Enabled; SelectTag(ModeCombo, _config.WorkMode.ToString()); SelectTag(FrameCombo, _config.FrameMode.ToString()); SelectText(EncodingCombo, _config.Encoding);
        ListenAddressText.Text = _config.ListenAddress; ListenPortText.Text = _config.ListenPort.ToString(); RemoteAddressText.Text = _config.RemoteAddress; RemotePortText.Text = _config.RemotePort.ToString(); MaxConnectionsText.Text = _config.MaxConnections.ToString(); TerminatorText.Text = _config.MessageTerminator; MaxMessageText.Text = _config.MaxMessageBytes.ToString(); ReceiveTimeoutText.Text = _config.ReceiveTimeoutMs.ToString(); AutoReconnectCheck.IsChecked = _config.AutoReconnect; UpdateModeVisibility();
    }
    private void ReadControls()
    {
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
        ProjectCombo.SelectedItem = profile;
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
        ProjectCombo.SelectedIndex = 0;
        StateText.Text = $"已删除 TCP/IP 项目：{profile.Name}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { ReadControls(); Directory.CreateDirectory(Path.GetDirectoryName(_configFile)!); var all = File.Exists(_configFile) ? JsonSerializer.Deserialize<Dictionary<string, ProjectCommunicationConfig>>(File.ReadAllText(_configFile)) ?? [] : []; all[_config.ProjectCode] = _config; File.WriteAllText(_configFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true })); StateText.Text = "配置已保存"; } catch (Exception ex) { ThemedMessageBox.Show(ex.Message, "保存通讯配置失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void Start_Click(object sender, RoutedEventArgs e) { try { ReadControls(); await AppServices.Instance.TcpCommunication.StartAsync(_config); StateText.Text = !_config.Enabled || _config.WorkMode == TcpWorkMode.Disabled ? "TCP/IP 通讯未启用，请先勾选启用 TCP/IP 通讯并选择工作模式" : "已启动"; } catch (Exception ex) { ThemedMessageBox.Show(ex.Message, "启动 TCP/IP 失败", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private async void Stop_Click(object sender, RoutedEventArgs e) { await AppServices.Instance.TcpCommunication.StopAsync(); StateText.Text = "已停止"; }
    private async void Send_Click(object sender, RoutedEventArgs e)
    { try { var connection = (ConnectionsList.SelectedItem as TcpConnectionInfo)?.ConnectionId ?? (AppServices.Instance.TcpCommunication.Connections.FirstOrDefault()?.ConnectionId ?? "client"); await AppServices.Instance.TcpCommunication.SendTextAsync(connection, ManualText.Text); } catch (Exception ex) { ThemedMessageBox.Show(ex.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e) => UpdateModeVisibility();
    private void TriggerTask_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTriggerEditor) return;
        if (TriggerTaskCombo.SelectedItem is not TaskEntity task) return;
        LoadTriggerEditor(task);
    }
    private void LoadTriggerEditor(TaskEntity task)
    {
        TaskTcpTriggerConfig config;
        try { config = JsonSerializer.Deserialize<TaskTcpTriggerConfig>(task.TriggerJson ?? "") ?? new TaskTcpTriggerConfig(); }
        catch (JsonException) { config = new TaskTcpTriggerConfig(); }
        TriggerEnabledCheck.IsChecked = config.Enabled;
        SelectTag(TriggerMatchModeCombo, config.MatchMode.ToString());
        TriggerMatchValueText.Text = config.MatchValue;
        TriggerResponseTemplateText.Text = config.ResponseTemplate;
    }
    private void TriggerRule_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TriggerRulesList.SelectedItem is not TriggerRuleRow row) return;
        var task = _triggerTasks.FirstOrDefault(item => item.Id == row.TaskId);
        if (task is null) return;
        _loadingTriggerEditor = true;
        TriggerTaskCombo.SelectedItem = task;
        _loadingTriggerEditor = false;
        LoadTriggerEditor(task);
    }
    private void NewTaskTrigger_Click(object sender, RoutedEventArgs e)
    {
        var configuredIds = TriggerRulesList.Items.OfType<TriggerRuleRow>().Select(row => row.TaskId).ToHashSet();
        var task = _triggerTasks.FirstOrDefault(item => !configuredIds.Contains(item.Id)) ?? _triggerTasks.FirstOrDefault();
        if (task is null) { ThemedMessageBox.Show("请先创建检测任务", "TCP/IP 设置"); return; }
        TriggerRulesList.SelectedItem = null;
        _loadingTriggerEditor = true;
        TriggerTaskCombo.SelectedItem = task;
        _loadingTriggerEditor = false;
        TriggerEnabledCheck.IsChecked = true;
        TriggerMatchModeCombo.SelectedIndex = 0;
        TriggerMatchValueText.Text = $"START_{task.StationCode}";
        TriggerResponseTemplateText.Text = new TaskTcpTriggerConfig().ResponseTemplate;
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
        if (TriggerTaskCombo.SelectedItem is not TaskEntity task)
        {
            ThemedMessageBox.Show("请先选择检测任务", "TCP/IP 设置");
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
            && existing.MatchMode == config.MatchMode
            && string.Equals(existing.MatchValue, config.MatchValue, StringComparison.Ordinal)))
        {
            ThemedMessageBox.Show("该触发消息已被其他任务使用，请为每个任务配置唯一消息", "TCP/IP 设置");
            return;
        }
        task.TriggerJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await AppServices.Instance.Tasks.SaveAsync(task);
        RefreshTriggerRules();
        StateText.Text = $"已保存任务触发：{task.Name}";
    }
    private void RefreshTriggerRules()
    {
        TriggerRulesList.ItemsSource = _triggerTasks
            .Select(task => (Task: task, Config: TryReadTrigger(task.TriggerJson)))
            .Where(pair => pair.Config is { Enabled: true })
            .Select(pair => new TriggerRuleRow(pair.Task.Id,
                $"{pair.Task.Name}  ·  {pair.Config!.MatchMode}  ·  {pair.Config.MatchValue}"))
            .ToArray();
    }
    private static TaskTcpTriggerConfig? TryReadTrigger(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TaskTcpTriggerConfig>(json); }
        catch (JsonException) { return null; }
    }
    private void UpdateModeVisibility() { var tag = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(); var server = tag == "Server"; ListenAddressText.IsEnabled = server; ListenPortText.IsEnabled = server; MaxConnectionsText.IsEnabled = server; RemoteAddressText.IsEnabled = tag == "Client"; RemotePortText.IsEnabled = tag == "Client"; }
    private void Tcp_LogReceived(object? sender, TcpLogEntry e) => Dispatcher.Invoke(() => { LogText.AppendText($"[{e.Timestamp:HH:mm:ss}] {e.Level} {e.Direction} {e.Message}{Environment.NewLine}"); LogText.ScrollToEnd(); ConnectionsList.ItemsSource = AppServices.Instance.TcpCommunication.Connections.ToArray(); });
    private void Tcp_StateChanged(object? sender, EventArgs e) => Dispatcher.Invoke(() => { StateText.Text = AppServices.Instance.TcpCommunication.State.ToString(); ConnectionsList.ItemsSource = AppServices.Instance.TcpCommunication.Connections.ToArray(); });
    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var value) ? value : fallback;
    private static int ParsePort(string text, int fallback) => Math.Clamp(ParseInt(text, fallback), 1, 65535);
    private static void SelectTag(ComboBox box, string tag) { box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)); }
    private static void SelectText(ComboBox box, string text) { box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase)) ?? box.Items[0]; }
}
