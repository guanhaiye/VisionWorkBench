using System.Windows;
using System.Windows.Controls;

namespace VisionWorkbench.App;

public partial class DatasetTypeDialog : Window
{
    public string? SelectedTaskType { get; private set; }

    public DatasetTypeDialog(bool segmentationOnly = false)
    {
        InitializeComponent();
        if (segmentationOnly)
        {
            Title = "选择分割标注类型";
            DetectionButton.Visibility = Visibility.Collapsed;
        }
    }

    private void TaskType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string taskType }) return;
        SelectedTaskType = taskType;
        DialogResult = true;
    }
}
