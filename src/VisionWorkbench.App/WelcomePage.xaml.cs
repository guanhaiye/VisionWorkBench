using System.Reflection;
using System.Windows.Controls;

namespace VisionWorkbench.App;

public partial class WelcomePage : UserControl
{
    public WelcomePage()
    {
        InitializeComponent();
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = version is null
            ? "版本 0.1.0"
            : $"版本 {version.Major}.{version.Minor}.{version.Build}";
    }
}
