using System.Windows;

namespace VisionWorkbench.App;

public partial class ModelRenameDialog : Window
{
    public string ModelName => NameText.Text.Trim();

    public ModelRenameDialog(string currentName)
    {
        InitializeComponent();
        NameText.Text = currentName;
        NameText.SelectAll();
        Loaded += (_, _) => NameText.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ModelName))
        {
            ThemedMessageBox.Show("模型节点名称不能为空。", "重命名模型节点", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
