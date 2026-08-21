using System.Windows;
using System.Windows.Controls;

namespace VisionWorkbench.App;

public partial class Shell : Window
{
    private readonly Dictionary<string, Func<object>> _pages = new();
    private readonly Dictionary<string, object> _cache = new();

    public Shell()
    {
        InitializeComponent();
        _pages["live"] = () => new LivePage();
        _pages["tasks"] = () => new TasksPage();
        _pages["datasets"] = () => new DatasetAnnotationPage();
        _pages["stations"] = () => new ProjectStationsPage();
        _pages["history"] = () => new HistoryPage();
        _pages["plugins"] = () => new PluginsPage();
        _pages["devices"] = () => new DevicesPage();
        _pages["settings"] = () => new SettingsPage();
        ShowPage("live");
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem item && item.Tag is string key)
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
}
