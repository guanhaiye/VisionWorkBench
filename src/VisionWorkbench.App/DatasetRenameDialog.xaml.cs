using System.Windows;

namespace VisionWorkbench.App;

public partial class DatasetRenameDialog : Window
{
    public string DatasetName => NameText.Text.Trim();

    public DatasetRenameDialog(string currentName)
    {
        InitializeComponent();
        NameText.Text = currentName;
        NameText.SelectAll();
        Loaded += (_, _) => NameText.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DatasetName))
        {
            ThemedMessageBox.Show("数据集名称不能为空。", "数据集", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
