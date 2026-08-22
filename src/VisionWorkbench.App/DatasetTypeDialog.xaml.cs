using System.Windows;
using System.Windows.Controls;

namespace VisionWorkbench.App;

public partial class DatasetTypeDialog : Window
{
    public string? SelectedTaskType { get; private set; }

    public DatasetTypeDialog()
    {
        InitializeComponent();
    }

    private void TaskType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string taskType }) return;
        SelectedTaskType = taskType;
        DialogResult = true;
    }
}
