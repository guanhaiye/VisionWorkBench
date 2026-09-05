using System.Windows;

namespace VisionWorkbench.App;

public partial class ReInferenceDialog : Window
{
    public string ModelVersion => ModelVersionText.Text.Trim();
    public string SettingsJson => SettingsText.Text.Trim();

    public ReInferenceDialog(string defaultSettings)
    {
        InitializeComponent();
        SettingsText.Text = string.IsNullOrWhiteSpace(defaultSettings) ? "{}" : defaultSettings;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ModelVersion))
        {
            ThemedMessageBox.Show("模型版本不能为空", "校验失败");
            return;
        }
        DialogResult = true;
    }
}
