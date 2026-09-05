using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace VisionWorkbench.App;

public partial class UserManagementWindow : Window
{
    private readonly string _adminName;
    private readonly ObservableCollection<UserAccount> _accounts = [];
    private UserAccount? _selectedAccount;
    private bool _isNewUser;

    public UserManagementWindow(string adminName)
    {
        InitializeComponent();
        _adminName = adminName;
        LoadAccounts();
        PrepareNewUser();
    }

    private void LoadAccounts()
    {
        _accounts.Clear();
        foreach (var account in AppServices.Instance.Settings.Accounts.OrderBy(account => account.UserName))
        {
            _accounts.Add(account);
        }
        UsersGrid.ItemsSource = _accounts;
    }

    private void PrepareNewUser()
    {
        _isNewUser = true;
        _selectedAccount = null;
        EditorTitle.Text = "新建用户";
        AccountNameText.Clear();
        AccountNameText.IsReadOnly = false;
        AccountRoleCombo.SelectedIndex = 0;
        AccountRoleCombo.IsEnabled = true;
        AccountPasswordBox.Clear();
        AccountEnabledCheck.IsChecked = true;
        UsersGrid.SelectedItem = null;
    }

    private void UsersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UsersGrid.SelectedItem is not UserAccount account) return;

        _isNewUser = false;
        _selectedAccount = account;
        EditorTitle.Text = account.IsSuperAdmin ? "编辑超级管理员" : "编辑用户";
        AccountNameText.Text = account.UserName;
        AccountNameText.IsReadOnly = true;
        AccountRoleCombo.SelectedItem = AccountRoleCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), account.Role, StringComparison.OrdinalIgnoreCase));
        AccountRoleCombo.IsEnabled = !account.IsSuperAdmin;
        AccountPasswordBox.Clear();
        AccountEnabledCheck.IsChecked = account.IsEnabled;
    }

    private void NewUser_Click(object sender, RoutedEventArgs e) => PrepareNewUser();

    private void SaveUser_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Instance.Settings;
        var name = AccountRules.NormalizeUserName(AccountNameText.Text);
        if (!AccountRules.ValidateUserName(name, out var usernameError))
        {
            ThemedMessageBox.Show(usernameError, "用户管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_isNewUser && settings.Accounts.Any(account =>
                string.Equals(account.UserName, name, StringComparison.OrdinalIgnoreCase)))
        {
            ThemedMessageBox.Show("用户名已存在，请更换用户名。", "用户管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var password = AccountPasswordBox.Password;
        if (_isNewUser && !AccountRules.ValidatePassword(password, out var requiredPasswordError))
        {
            ThemedMessageBox.Show(requiredPasswordError, "用户管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!_isNewUser && !string.IsNullOrEmpty(password)
            && !AccountRules.ValidatePassword(password, out var passwordError))
        {
            ThemedMessageBox.Show(passwordError, "用户管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var account = _selectedAccount;
        if (_isNewUser)
        {
            account = new UserAccount { UserName = name };
            settings.Accounts.Add(account);
        }
        else if (account is null)
        {
            ThemedMessageBox.Show("请先选择一个用户。", "用户管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        account.Role = account.IsSuperAdmin
            ? "superadmin"
            : (AccountRoleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "operator";
        account.IsEnabled = AccountEnabledCheck.IsChecked == true;
        if (!string.IsNullOrEmpty(password)) account.PasswordHash = AccountRules.HashPassword(password);
        if (string.Equals(account.UserName, _adminName, StringComparison.OrdinalIgnoreCase))
        {
            settings.OperatorName = account.UserName;
            settings.CurrentRole = account.Role;
            settings.AvatarPath = account.AvatarPath;
            settings.PasswordHash = account.PasswordHash;
        }

        AppServices.Instance.SaveUserSettings();

        UsersGrid.Items.Refresh();
        if (_isNewUser) UsersGrid.SelectedItem = account;
        ThemedMessageBox.Show("用户信息已保存。", "用户管理", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAccount is null)
        {
            ThemedMessageBox.Show("请先选择要删除的账号。", "用户管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.Equals(_selectedAccount.UserName, _adminName, StringComparison.OrdinalIgnoreCase))
        {
            ThemedMessageBox.Show("不能删除当前登录的超级管理员。", "用户管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_selectedAccount.IsSuperAdmin
            && AppServices.Instance.Settings.Accounts.Count(account => account.IsSuperAdmin) <= 1)
        {
            ThemedMessageBox.Show("系统至少需要保留一个超级管理员。", "用户管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = new ConfirmDialog("删除用户", $"确定删除账号“{_selectedAccount.UserName}”吗？此操作不可恢复。")
        {
            Owner = this,
        };
        if (confirm.ShowDialog() != true) return;

        AppServices.Instance.Settings.Accounts.Remove(_selectedAccount);
        AppServices.Instance.SaveUserSettings();
        LoadAccounts();
        PrepareNewUser();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
