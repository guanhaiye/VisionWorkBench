using System.Collections.ObjectModel;
using System.Windows;

namespace VisionWorkbench.App;

public partial class AnnotationClassDialog : Window
{
    private readonly ObservableCollection<string> _classes;

    public string? SelectedClassName { get; private set; }

    public AnnotationClassDialog(IEnumerable<string> classes)
    {
        InitializeComponent();
        _classes = new ObservableCollection<string>(classes
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        ExistingClassCombo.ItemsSource = _classes;
        if (_classes.Count > 0) ExistingClassCombo.SelectedIndex = 0;
    }

    private void CreateClass_Click(object sender, RoutedEventArgs e)
    {
        var className = NewClassTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(className))
        {
            ThemedMessageBox.Show(this, "请输入新类别名称。", "标注类别",
                MessageBoxButton.OK, MessageBoxImage.Information);
            NewClassTextBox.Focus();
            return;
        }
        if (_classes.Any(value => string.Equals(value, className, StringComparison.OrdinalIgnoreCase)))
        {
            ExistingClassCombo.SelectedItem = _classes.First(value =>
                string.Equals(value, className, StringComparison.OrdinalIgnoreCase));
            NewClassTextBox.Clear();
            return;
        }

        _classes.Add(className);
        ExistingClassCombo.SelectedItem = className;
        NewClassTextBox.Clear();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var selected = ExistingClassCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(selected))
        {
            ThemedMessageBox.Show(this, "请选择或创建一个类别。", "标注类别",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedClassName = selected.Trim();
        DialogResult = true;
    }
}
