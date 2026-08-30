using System.Windows;
using System.Windows.Controls;

namespace VisionWorkbench.App;

public partial class Shell : Window
{
    private readonly Dictionary<string, Func<object>> _pages = new();
    private readonly Dictionary<string, object> _cache = new();
    private readonly HashSet<ListBox> _navigationLists = [];
    private bool _updatingNavigationSelection;

    public Shell()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaximizeButton();
        _pages["welcome"] = () => new WelcomePage();
        _pages["live"] = () => new LivePage();
        _pages["tasks"] = () => new TasksPage();
        _pages["detection-annotation"] = () => new DatasetAnnotationPage(AnnotationPlatform.Detection);
        _pages["segmentation-annotation"] = () => new DatasetAnnotationPage(AnnotationPlatform.Segmentation);
        _pages["training"] = () => new TrainingPage();
        _pages["behavior-annotation"] = () => new BehaviorAnnotationPage();
        _pages["behavior-collection"] = () => new BehaviorCollectionPage();
        _pages["behavior-training"] = () => new BehaviorTrainingPage();
        _pages["stations"] = () => new ProjectStationsPage();
        _pages["communication"] = () => new CommunicationPage();
        _pages["history"] = () => new HistoryPage();
        _pages["logs"] = () => new LogPage();
        _pages["spc"] = () => new SpcPage();
        _pages["plugins"] = () => new PluginsPage();
        _pages["devices"] = () => new DevicesPage();
        _pages["settings"] = () => new SettingsPage();
        RefreshUserHeader();
        ShowPage("welcome");
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox currentList)
        {
            _navigationLists.Add(currentList);
        }
        if (e.AddedItems.OfType<ListBoxItem>().FirstOrDefault() is { Tag: string key })
        {
            if (!_updatingNavigationSelection && sender is ListBox selectedList)
            {
                _updatingNavigationSelection = true;
                try
                {
                    foreach (var list in _navigationLists.Where(list => !ReferenceEquals(list, selectedList)))
                    {
                        list.SelectedIndex = -1;
                    }
                }
                finally
                {
                    _updatingNavigationSelection = false;
                }
            }
            ShowPage(key);
        }
    }

    private void NavItem_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 已选中的项目再次点击时 SelectionChanged 不会触发，直接按 Tag 恢复对应页面。
        if (sender is ListBoxItem { Tag: string key })
        {
            ShowPage(key);
        }
    }

    private void ShowPage(string key)
    {
        // XAML IsSelected=True 在 InitializeComponent 期间触发 SelectionChanged，此时注册表尚未填充
        if (!_pages.TryGetValue(key, out var factory))
        {
            return;
        }
        if (!_cache.TryGetValue(key, out var page))
        {
            page = factory();
            _cache[key] = page;
        }
        PageHost.Content = page;
        if (page is LivePage live)
        {
            live.OnShown();
        }
    }

    private void UserMenu_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Instance.Settings;
        UserNameText.Text = settings.OperatorName == "未登录" ? "" : settings.OperatorName;
        UserRoleCombo.SelectedItem = UserRoleCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), settings.CurrentRole, StringComparison.OrdinalIgnoreCase));
        if (UserRoleCombo.SelectedItem is null) UserRoleCombo.SelectedIndex = 0;
        UserMenuPopup.IsOpen = true;
    }

    private void SaveUser_Click(object sender, RoutedEventArgs e)
    {
        var name = UserNameText.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("请输入姓名", "用户管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var settings = AppServices.Instance.Settings;
        settings.OperatorName = name;
        settings.CurrentRole = (UserRoleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "operator";
        AppServices.Instance.SaveUserSettings();
        RefreshUserHeader();
        UserMenuPopup.IsOpen = false;
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Instance.Settings;
        settings.OperatorName = "未登录";
        settings.CurrentRole = "operator";
        AppServices.Instance.SaveUserSettings();
        RefreshUserHeader();
        UserMenuPopup.IsOpen = false;
    }

    private void RefreshUserHeader()
    {
        var settings = AppServices.Instance.Settings;
        var loggedIn = !string.IsNullOrWhiteSpace(settings.OperatorName) && settings.OperatorName != "未登录";
        CurrentUserText.Text = loggedIn ? settings.OperatorName : "用户管理";
        CurrentRoleText.Text = loggedIn ? RoleDisplayName(settings.CurrentRole) : "点击登录或设置个人信息";
        UserAvatarText.Text = loggedIn ? settings.OperatorName.Trim()[0].ToString() : "用";
    }

    private static string RoleDisplayName(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "expert" => "专家",
        "engineer" => "工程师",
        _ => "操作员",
    };

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            UpdateMaximizeButton();
            e.Handled = true;
            return;
        }
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeButton()
    {
        if (MaximizeButton is null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Data = System.Windows.Media.Geometry.Parse(maximized
            ? "M 3,1 L 11,1 11,9 M 1,3 L 9,3 9,11 1,11 Z"
            : "M 1,1 L 11,1 11,11 1,11 Z");
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
    }
}
