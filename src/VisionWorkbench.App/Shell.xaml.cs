using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace VisionWorkbench.App;

public partial class Shell : Window
{
    private readonly Dictionary<string, Func<object>> _pages = new();
    private readonly Dictionary<string, object> _cache = new();
    private readonly HashSet<ListBox> _navigationLists = [];
    private bool _updatingNavigationSelection;
    private string? _avatarPathDraft;
    private bool _isUserLoggedIn;
    private bool _registrationMode;
    private string? _currentAccountName;
    private bool _isSuperAdmin;
    private bool _updatingLoginOptions;

    private void BrandLogoImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image) return;
        var rotation = new RotateTransform();
        image.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(5),
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    public Shell()
    {
        InitializeComponent();
        PreferApplicationThemeResources();
        ThemeManager.SyncWindowResources(this);
        AddHandler(System.Windows.Input.Mouse.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler(Shell_PreviewMouseWheel), handledEventsToo: true);
        Topmost = false;
        UpdateTopmostButton();
        if (SidebarStatusStackPanel.Children.Count > 2)
        {
            SidebarStatusStackPanel.Children[2].Visibility = Visibility.Collapsed;
        }
        // 自定义标题栏不使用系统默认的窗口位置恢复；启动时始终将主窗口放回当前主屏工作区，
        // 避免上次异常退出后留下屏幕外或仅剩标题栏大小的窗口。
        WindowStartupLocation = WindowStartupLocation.Manual;
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
        WindowState = WindowState.Normal;
        SourceInitialized += Shell_SourceInitialized;
        StateChanged += (_, _) => UpdateMaximizeButton();
        _pages["welcome"] = () => new WelcomePage();
        _pages["live"] = () => new LivePage();
        _pages["tasks"] = () => new TasksPage();
        _pages["sop"] = () => new SopPage();
        _pages["detection-annotation"] = () => new DatasetAnnotationPage(AnnotationPlatform.Detection);
        _pages["segmentation-annotation"] = () => new DatasetAnnotationPage(AnnotationPlatform.Segmentation);
        _pages["semantic-segmentation-annotation"] = () => new DatasetAnnotationPage(AnnotationPlatform.SemanticSegmentation);
        _pages["instance-segmentation-annotation"] = () => new DatasetAnnotationPage(AnnotationPlatform.InstanceSegmentation);
        _pages["pose-annotation"] = () => new PoseAnnotationPage();
        _pages["training"] = () => new TrainingPage();
        _pages["model-test"] = () => new ModelTestPage(false, "YOLO11 目标检测", "detection");
        _pages["data-model"] = () => new DataModelPage();
        _pages["behavior-annotation"] = () => new BehaviorAnnotationPage();
        _pages["behavior-collection"] = () => new BehaviorCollectionPage();
        _pages["behavior-training"] = () => new BehaviorTrainingPage();
        _pages["communication"] = () => new CommunicationPage();
        _pages["history"] = () => new HistoryPage();
        _pages["logs"] = () => new LogPage();
        _pages["plugins"] = () => new PluginsPage();
        _pages["devices"] = () => new DevicesPage();
        _pages["monitor"] = () => new SystemMonitorPage();
        _pages["license"] = () => new LicensePage();
        _pages["settings"] = () => new SettingsPage();
        TryAutoLogin();
        RefreshUserHeader();
        var license = AppServices.Instance.License.Validate();
        ShowPage(license.IsValid ? "welcome" : "license");
        ApplyLicenseAvailability(license.IsValid);
        if (!license.IsValid)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                ThemedMessageBox.Show(this,
                    "当前电脑缺少有效许可证。请在许可证管理界面复制申请码，发送给授权方获取许可证。",
                    "缺少许可证", MessageBoxButton.OK, MessageBoxImage.Warning)));
        }
    }

    private void PreferApplicationThemeResources()
    {
        // 正常应用启动时，Application.Resources 已加载并由 ThemeManager 管理。
        // Shell.xaml 中的 Theme.xaml 仅作为独立设计器/UI 测试的兜底；保留两份会
        // 让页面文字读取到未切换的局部主题色，造成日间模式下文字发白。
        if (System.Windows.Application.Current?.Resources.Contains("TextBrush") != true) return;

        var localTheme = Resources.MergedDictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (localTheme is not null)
        {
            Resources.MergedDictionaries.Remove(localTheme);
        }
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

    private void Shell_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!SidebarScrollViewer.IsMouseOver || e.Delta == 0)
        {
            return;
        }

        // 由窗口统一接管侧边栏滚轮，避免 Expander/ListBox 内部控件吞掉事件。
        SidebarScrollViewer.UpdateLayout();
        if (SidebarScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var offset = SidebarScrollViewer.VerticalOffset - Math.Sign(e.Delta) * 54;
        SidebarScrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, SidebarScrollViewer.ScrollableHeight));
        e.Handled = true;
    }

    private void SidebarExpander_Expanded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                SidebarScrollViewer.UpdateLayout();
                SidebarScrollViewer.ScrollToEnd();
            }));
    }

    private void ShowPage(string key)
    {
        if (!IsLicenseExemptPage(key) && !AppServices.Instance.License.Validate().IsValid)
        {
            ShowPage("license");
            return;
        }
        if (key.StartsWith("data-model-", StringComparison.OrdinalIgnoreCase))
        {
            var category = key["data-model-".Length..];
            if (!_cache.TryGetValue("data-model", out var dataModel))
            {
                dataModel = new DataModelPage();
                _cache["data-model"] = dataModel;
            }
            PageHost.Content = dataModel;
            if (dataModel is FrameworkElement dataModelElement)
            {
                ThemeManager.ApplyPageTextBrush(dataModelElement);
            }
            if (dataModel is DataModelPage dataModelPage)
            {
                dataModelPage.SelectCategory(category);
            }
            return;
        }

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
        if (page is FrameworkElement pageElement)
        {
            ThemeManager.ApplyPageTextBrush(pageElement);
        }
        if (page is LivePage live)
        {
            live.OnShown();
        }
    }

    private static bool IsLicenseExemptPage(string key) =>
        key is "welcome" or "license" or "settings" or "logs" or "monitor";

    internal void ApplyLicenseAvailability(bool isLicensed)
    {
        UserMenuButton.IsEnabled = isLicensed;
        SystemManagementExpander.IsExpanded = !isLicensed;
        var navigationLists = new[]
        {
            WorkspaceNavList, DataModelNavList, ResultsNavList, CommunicationNavList, SystemNavList,
        };
        foreach (var item in navigationLists.SelectMany(list => list.Items.OfType<ListBoxItem>()))
        {
            item.IsEnabled = isLicensed || string.Equals(item.Tag?.ToString(), "license", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void UserMenu_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Instance.Settings;
        UserNameText.Text = settings.OperatorName == "未登录" ? "" : settings.OperatorName;
        _avatarPathDraft = settings.AvatarPath;
        LoadLoginPreferences();
        _registrationMode = false;
        UserRoleCombo.SelectedItem = UserRoleCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), settings.CurrentRole, StringComparison.OrdinalIgnoreCase));
        if (UserRoleCombo.SelectedItem is null) UserRoleCombo.SelectedIndex = 0;
        RefreshAvatarPreview();
        ConfigureUserPopup(IsUserRegistered(settings));
        UserMenuPopup.IsOpen = true;
    }

    private void ConfigureUserPopup(bool registered)
    {
        if (UserPopupContentBorder.Child is not StackPanel content
            || content.Children.Count < 2
            || content.Children[0] is not Grid header
            || header.Children.Count < 1
            || header.Children[0] is not StackPanel headerText
            || content.Children[1] is not StackPanel form
            || content.Children[2] is not StackPanel loginPanel
            || form.Children.Count < 10)
        {
            return;
        }

        var showInfo = _isUserLoggedIn;
        var showRegistration = !showInfo && _registrationMode && !registered;
        form.Visibility = showInfo || showRegistration ? Visibility.Visible : Visibility.Collapsed;
        loginPanel.Visibility = showInfo || showRegistration ? Visibility.Collapsed : Visibility.Visible;

        if (headerText.Children.Count > 0 && headerText.Children[0] is TextBlock title)
        {
            title.Text = registered ? "用户信息" : "用户注册";
        }
        if (headerText.Children.Count > 1 && headerText.Children[1] is TextBlock description)
        {
            description.Text = registered ? "当前登录用户信息（只读）" : "首次使用请注册本机用户信息";
        }

        if (headerText.Children.Count > 0 && headerText.Children[0] is TextBlock currentTitle)
        {
            currentTitle.Text = showInfo ? "用户信息" : showRegistration ? "用户注册" : "用户登录";
        }
        if (headerText.Children.Count > 1 && headerText.Children[1] is TextBlock currentDescription)
        {
            currentDescription.Text = showInfo ? "当前登录用户信息（只读）"
                : showRegistration ? "首次使用请注册本机用户信息" : "请输入已注册的用户信息";
        }

        if (form.Children[0] is FrameworkElement oldTitle)
        {
            oldTitle.Visibility = Visibility.Collapsed;
        }
        if (form.Children[1] is FrameworkElement oldDescription)
        {
            oldDescription.Visibility = Visibility.Collapsed;
        }

        UserNameText.IsReadOnly = registered;
        UserRoleCombo.IsEnabled = !registered;
        UserNameText.IsReadOnly = showInfo;
        UserRoleCombo.IsEnabled = showRegistration;
        AvatarPreviewBorder.IsHitTestVisible = showInfo || showRegistration;
        AvatarPreviewBorder.Cursor = showInfo || showRegistration
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;

        AvatarRegistrationHint.Visibility = showRegistration ? Visibility.Visible : Visibility.Collapsed;
        RegistrationPasswordPanel.Visibility = showRegistration ? Visibility.Visible : Visibility.Collapsed;

        if (UserActionsPanel.Children.Count > 1)
        {
            var actions = UserActionsPanel;
            actions.Children[0].Visibility = showInfo ? Visibility.Visible : Visibility.Collapsed;
            actions.Children[1].Visibility = showRegistration ? Visibility.Visible : Visibility.Collapsed;
            if (actions.Children.Count > 2)
            {
                actions.Children[2].Visibility = showInfo && _isSuperAdmin
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (actions.Children[1] is Button saveButton)
            {
                saveButton.Content = "注册并保存";
            }
        }
    }

    private static bool IsUserRegistered(AppSettings settings) => settings.Accounts.Count > 0;

    private static UserAccount? FindAccount(AppSettings settings, string? userName) =>
        settings.Accounts.FirstOrDefault(account =>
            account.IsEnabled
            && string.Equals(account.UserName, AccountRules.NormalizeUserName(userName), StringComparison.OrdinalIgnoreCase));

    private void LoadLoginPreferences()
    {
        var settings = AppServices.Instance.Settings;
        LoginNameText.Text = string.IsNullOrWhiteSpace(settings.RememberedLoginUserName)
            ? settings.OperatorName
            : settings.RememberedLoginUserName;
        LoginPasswordBox.Password = settings.RememberLoginPassword
            ? UnprotectLoginPassword(settings.RememberedLoginPassword) ?? ""
            : "";
        _updatingLoginOptions = true;
        RememberPasswordCheckBox.IsChecked = settings.RememberLoginPassword;
        AutoLoginCheckBox.IsChecked = settings.AutoLogin;
        _updatingLoginOptions = false;
    }

    private void RememberPasswordOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingLoginOptions || RememberPasswordCheckBox.IsChecked == true) return;
        _updatingLoginOptions = true;
        AutoLoginCheckBox.IsChecked = false;
        _updatingLoginOptions = false;
    }

    private void AutoLoginOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingLoginOptions || AutoLoginCheckBox.IsChecked != true) return;
        _updatingLoginOptions = true;
        RememberPasswordCheckBox.IsChecked = true;
        _updatingLoginOptions = false;
    }

    private bool TryAutoLogin()
    {
        var settings = AppServices.Instance.Settings;
        if (!settings.AutoLogin || !settings.RememberLoginPassword) return false;

        var password = UnprotectLoginPassword(settings.RememberedLoginPassword);
        var account = FindAccount(settings, settings.RememberedLoginUserName);
        if (account is null || password is null) return false;
        var authentication = AppServices.Instance.Identity.AuthenticateAsync(account.UserName, password).GetAwaiter().GetResult();
        if (!authentication.Succeeded) return false;

        ApplyLoggedInAccount(settings, account);
        return true;
    }

    private void ApplyLoggedInAccount(AppSettings settings, UserAccount account)
    {
        var effectiveRole = AppServices.Instance.Identity.GetEffectiveRoleAsync(account.UserName).GetAwaiter().GetResult();
        settings.OperatorName = account.UserName;
        settings.CurrentRole = effectiveRole.Role;
        settings.AvatarPath = account.AvatarPath;
        settings.PasswordHash = account.PasswordHash;
        _currentAccountName = account.UserName;
        _isSuperAdmin = effectiveRole.IsAdministrator;
        _isUserLoggedIn = true;
        _registrationMode = false;
        AppServices.Instance.Session.Begin(account.UserName, effectiveRole.Role, effectiveRole.IsAdministrator);
    }

    private void SaveLoginPreferences(string userName, string password)
    {
        var settings = AppServices.Instance.Settings;
        var remember = RememberPasswordCheckBox.IsChecked == true || AutoLoginCheckBox.IsChecked == true;
        var autoLogin = AutoLoginCheckBox.IsChecked == true;
        settings.RememberLoginPassword = remember;
        settings.AutoLogin = autoLogin;

        if (remember)
        {
            var protectedPassword = ProtectLoginPassword(password);
            if (protectedPassword is null)
            {
                settings.RememberLoginPassword = false;
                settings.AutoLogin = false;
                settings.RememberedLoginUserName = null;
                settings.RememberedLoginPassword = null;
                ThemedMessageBox.Show("当前系统无法安全保存登录密码，已关闭记住密码和自动登录。", "用户登录", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                settings.RememberedLoginUserName = userName;
                settings.RememberedLoginPassword = protectedPassword;
            }
        }
        else
        {
            settings.RememberedLoginUserName = null;
            settings.RememberedLoginPassword = null;
        }

        AppServices.Instance.SaveUserSettings();
    }

    private static string? ProtectLoginPassword(string password)
    {
        try
        {
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(password),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? UnprotectLoginPassword(string? protectedPassword)
    {
        if (string.IsNullOrWhiteSpace(protectedPassword)) return null;
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedPassword);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Instance.Settings;
        if (IsUserRegistered(settings))
        {
            ThemedMessageBox.Show("已有普通用户，请由超级管理员在“用户管理”中创建新账号。", "用户注册", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _registrationMode = true;
        _avatarPathDraft = null;
        UserNameText.Clear();
        UserRoleCombo.SelectedIndex = 0;
        RegistrationPasswordBox.Clear();
        RegistrationConfirmPasswordBox.Clear();
        RefreshAvatarPreview();
        ConfigureUserPopup(false);
        UserMenuPopup.IsOpen = true;
        UserNameText.Focus();
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Instance.Settings;
        var name = AccountRules.NormalizeUserName(LoginNameText.Text);
        var password = LoginPasswordBox.Password;
        var authentication = await AppServices.Instance.Identity.AuthenticateAsync(name, password);
        var account = FindAccount(settings, name);
        if (authentication.Succeeded && account is not null)
        {
            ApplyLoggedInAccount(settings, account);
            SaveLoginPreferences(account.UserName, password);
            RefreshUserHeader();
            UserMenuPopup.IsOpen = false;
            return;
        }

        ThemedMessageBox.Show(authentication.Reason, "用户登录", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void AvatarPreview_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isUserLoggedIn || _registrationMode)
        {
            ChooseAvatar();
            e.Handled = true;
        }
    }

    private void ChooseAvatar_Click(object sender, RoutedEventArgs e) => ChooseAvatar();

    private void ChooseAvatar()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择头像",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        var wasOpen = UserMenuPopup.IsOpen;
        UserMenuPopup.IsOpen = false;
        try
        {
            if (dialog.ShowDialog(this) == true)
            {
                _avatarPathDraft = dialog.FileName;
                if (_isUserLoggedIn && !string.IsNullOrWhiteSpace(_currentAccountName))
                {
                    var settings = AppServices.Instance.Settings;
                    var account = settings.Accounts.FirstOrDefault(item =>
                        string.Equals(item.UserName, _currentAccountName, StringComparison.OrdinalIgnoreCase));
                    if (account is not null) account.AvatarPath = _avatarPathDraft;
                    settings.AvatarPath = _avatarPathDraft;
                    AppServices.Instance.SaveUserSettings();
                    RefreshUserHeader();
                }
                RefreshAvatarPreview();
            }
        }
        finally
        {
            ConfigureUserPopup(IsUserRegistered(AppServices.Instance.Settings));
            if (wasOpen)
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        ConfigureUserPopup(IsUserRegistered(AppServices.Instance.Settings));
                        UserMenuPopup.IsOpen = true;
                        UserNameText.Focus();
                    }));
            }
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        HelpMenuPopup.IsOpen = true;
    }

    private void HelpButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HelpButton_Click(sender, e);
        e.Handled = true;
    }

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdateTopmostButton();
    }

    private void TopmostButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        TopmostButton_Click(sender, e);
        e.Handled = true;
    }

    private void UpdateTopmostButton()
    {
        if (TopmostButton is null) return;
        TopmostButton.ToolTip = Topmost ? "取消窗口置顶" : "窗口置顶";
        TopmostInactiveIcon.Visibility = Topmost ? Visibility.Collapsed : Visibility.Visible;
        TopmostActiveIconBackground.Visibility = Topmost ? Visibility.Visible : Visibility.Collapsed;
        TopmostButton.Background = Topmost
            ? (System.Windows.Media.Brush)FindResource("AccentSoftBrush")
            : System.Windows.Media.Brushes.Transparent;
    }

    private void PythonScriptRules_Click(object sender, RoutedEventArgs e)
    {
        HelpMenuPopup.IsOpen = false;
        var dialog = new PythonScriptRulesDialog { Owner = this };
        dialog.ShowDialog();
    }

    private void SuperAdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSuperAdmin || !_isUserLoggedIn) return;
        UserMenuPopup.IsOpen = false;
        var window = new UserManagementWindow(_currentAccountName ?? AccountRules.SuperAdminName)
        {
            Owner = this,
        };
        window.ShowDialog();
        RefreshUserHeader();
    }

    private async void SaveUser_Click(object sender, RoutedEventArgs e)
    {
        if (!_registrationMode || IsUserRegistered(AppServices.Instance.Settings))
        {
            return;
        }

        var name = AccountRules.NormalizeUserName(UserNameText.Text);
        if (!AccountRules.ValidateUserName(name, out var usernameError))
        {
            ThemedMessageBox.Show(usernameError, "用户注册", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            ThemedMessageBox.Show("请输入姓名", "用户管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var settings = AppServices.Instance.Settings;
        var password = RegistrationPasswordBox.Password;
        if (!AccountRules.ValidatePassword(password, out var passwordError))
        {
            ThemedMessageBox.Show(passwordError, "用户注册", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (password.Length < 4)
        {
            ThemedMessageBox.Show("密码至少需要 4 位。", "用户注册", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!string.Equals(password, RegistrationConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            ThemedMessageBox.Show("两次输入的密码不一致。", "用户注册", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (settings.Accounts.Any(account =>
                string.Equals(account.UserName, name, StringComparison.OrdinalIgnoreCase)))
        {
            ThemedMessageBox.Show("用户名已存在，请更换用户名。", "用户注册", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var isFirstAccount = settings.Accounts.Count == 0;
        var account = new UserAccount
        {
            UserName = name,
            Role = isFirstAccount
                ? "superadmin"
                : (UserRoleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "operator",
            AvatarPath = _avatarPathDraft,
            PasswordHash = AccountRules.HashPassword(password),
            IsSuperAdmin = isFirstAccount,
            IsEnabled = true,
        };
        settings.Accounts.Add(account);
        settings.OperatorName = account.UserName;
        settings.CurrentRole = account.Role;
        settings.AvatarPath = account.AvatarPath;
        settings.PasswordHash = account.PasswordHash;
        AppServices.Instance.SaveUserSettings();
        await AppServices.Instance.Identity.UpsertAccountAsync(account);
        await AppServices.Instance.Audit.RecordAsync("user.create", "user", account.UserName,
            account.UserName, detailsJson: "{\"source\":\"registration\"}");
        _currentAccountName = account.UserName;
        _isSuperAdmin = account.IsSuperAdmin;
        _isUserLoggedIn = true;
        _registrationMode = false;
        RefreshUserHeader();
        UserMenuPopup.IsOpen = false;
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        _isUserLoggedIn = false;
        _registrationMode = false;
        AppServices.Instance.Session.End();
        RefreshUserHeader();
        UserMenuPopup.IsOpen = false;
    }

    private void RefreshUserHeader()
    {
        var settings = AppServices.Instance.Settings;
        var loggedIn = _isUserLoggedIn && !string.IsNullOrWhiteSpace(_currentAccountName);
        CurrentUserText.Text = loggedIn ? settings.OperatorName : "用户管理";
        CurrentRoleText.Text = loggedIn ? RoleDisplayName(settings.CurrentRole) : "点击登录或设置个人信息";
        UserAvatarText.Text = loggedIn ? settings.OperatorName.Trim()[0].ToString() : "用";
        ApplyAvatar(UserAvatarBorder, UserAvatarText, loggedIn ? settings.AvatarPath : null,
            loggedIn ? settings.OperatorName : "用户");
    }

    private void RefreshAvatarPreview()
    {
        var settings = AppServices.Instance.Settings;
        var name = string.IsNullOrWhiteSpace(UserNameText.Text) ? settings.OperatorName : UserNameText.Text.Trim();
        AvatarPreviewText.Text = string.IsNullOrWhiteSpace(name) ? "用" : name[0].ToString();
        ApplyAvatar(AvatarPreviewBorder, AvatarPreviewText, _avatarPathDraft, name);
    }

    private static void ApplyAvatar(Border border, TextBlock fallback, string? path, string? name)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                border.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                fallback.Visibility = Visibility.Collapsed;
                return;
            }
            catch
            {
                // 图片无法读取时回退到文字头像。
            }
        }

        border.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
        fallback.Text = string.IsNullOrWhiteSpace(name) ? "用" : name.Trim()[0].ToString();
        fallback.Visibility = Visibility.Visible;
    }

    private static string RoleDisplayName(string? role) => AccountRules.RoleDisplayName(role);

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        var result = ThemedMessageBox.Show(
            this,
            "确定要重启 VisionWorkbench 吗？",
            "重启确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException("无法确定当前软件路径");

            var escapedExecutable = executable.Replace("\"", "\\\"");
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c timeout /t 2 /nobreak >nul & start \"\" \"{escapedExecutable}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this, $"重启失败：{ex.Message}", "重启软件",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MinimizeWindow_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        MinimizeWindow_Click(sender, e);
        e.Handled = true;
    }

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

    private void MaximizeWindow_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        MaximizeWindow_Click(sender, e);
        e.Handled = true;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        var result = ThemedMessageBox.Show(
            "确定要退出 VisionWorkbench 吗？",
            "退出确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.OK)
        {
            Close();
        }
    }

    private void CloseWindow_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        CloseWindow_Click(sender, e);
        e.Handled = true;
    }

    private void UpdateMaximizeButton()
    {
        if (MaximizeButton is null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Data = System.Windows.Media.Geometry.Parse(maximized
            ? "M 3,1 L 11,1 11,9 M 1,3 L 9,3 9,11 1,11 Z"
            : "M 1,1 L 11,1 11,11 1,11 Z");
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
    }
    private void Shell_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowChromeHook);
        }
    }

    private IntPtr WindowChromeHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        const int WmNcHitTest = 0x0084;
        const int WmNcLButtonDown = 0x00A1;
        const int WmLButtonDown = 0x0201;
        if (message == WmGetMinMaxInfo)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return IntPtr.Zero;
            }

            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.ptMaxPosition.x = monitorInfo.rcWork.left - monitorInfo.rcMonitor.left;
            info.ptMaxPosition.y = monitorInfo.rcWork.top - monitorInfo.rcMonitor.top;
            info.ptMaxSize.x = monitorInfo.rcWork.right - monitorInfo.rcWork.left;
            info.ptMaxSize.y = monitorInfo.rcWork.bottom - monitorInfo.rcWork.top;
            Marshal.StructureToPtr(info, lParam, true);
            handled = true;
            return IntPtr.Zero;
        }

        if (message == WmNcHitTest)
        {
            if (GetCursorPos(out var hitTestCursor)
                && (IsScreenPointInside(HelpButton, hitTestCursor)
                    || IsScreenPointInside(TopmostButton, hitTestCursor)
                    || IsScreenPointInside(MinimizeButton, hitTestCursor)
                    || IsScreenPointInside(MaximizeButton, hitTestCursor)
                    || IsScreenPointInside(CloseButton, hitTestCursor)))
            {
                handled = true;
                return new IntPtr(1); // HTCLIENT: 让 WPF 按钮接收后续鼠标消息。
            }
            return IntPtr.Zero;
        }

        if (message != WmNcLButtonDown && message != WmLButtonDown)
        {
            return IntPtr.Zero;
        }

        if (!GetCursorPos(out var cursor))
        {
            return IntPtr.Zero;
        }

        if (IsScreenPointInside(HelpButton, cursor))
        {
            HelpButton_Click(this, new RoutedEventArgs());
            handled = true;
        }
        else if (IsScreenPointInside(TopmostButton, cursor))
        {
            TopmostButton_Click(this, new RoutedEventArgs());
            handled = true;
        }
        else if (IsScreenPointInside(MinimizeButton, cursor))
        {
            WindowState = WindowState.Minimized;
            handled = true;
        }
        else if (IsScreenPointInside(MaximizeButton, cursor))
        {
            MaximizeWindow_Click(this, new RoutedEventArgs());
            handled = true;
        }
        else if (IsScreenPointInside(CloseButton, cursor))
        {
            CloseWindow_Click(this, new RoutedEventArgs());
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static bool IsScreenPointInside(FrameworkElement element, NativePoint point)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = element.PointToScreen(new System.Windows.Point(element.ActualWidth, element.ActualHeight));
        return point.x >= topLeft.X && point.x < bottomRight.X
            && point.y >= topLeft.Y && point.y < bottomRight.Y;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }
}
