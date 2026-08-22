using System.Windows;

namespace VisionWorkbench.App;

public partial class DatasetSplitDialog : Window
{
    public double TrainRatio => TrainRatioSlider.Value / 100d;

    public DatasetSplitDialog()
    {
        InitializeComponent();
        UpdateRatioText();
    }

    private void TrainRatioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateRatioText();

    private void UpdateRatioText()
    {
        if (RatioText is null || TrainRatioSlider is null) return;
        var train = (int)Math.Round(TrainRatioSlider.Value);
        RatioText.Text = $"训练集 {train}%  /  验证集 {100 - train}%";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
