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
        _pages["training"] = () => new TrainingPage();
        _pages["behavior-annotation"] = () => new BehaviorAnnotationPage();
        _pages["behavior-collection"] = () => new BehaviorCollectionPage();
        _pages["behavior-training"] = () => new BehaviorTrainingPage();
        _pages["stations"] = () => new ProjectStationsPage();
        _pages["history"] = () => new HistoryPage();
        _pages["logs"] = () => new LogPage();
        _pages["spc"] = () => new SpcPage();
        _pages["plugins"] = () => new PluginsPage();
        _pages["devices"] = () => new DevicesPage();
        _pages["settings"] = () => new SettingsPage();
        ShowPage("datasets");
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<ListBoxItem>().FirstOrDefault() is { Tag: string key })
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
