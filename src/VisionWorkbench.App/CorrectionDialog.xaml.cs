using System.Windows;

namespace VisionWorkbench.App;

/// <summary>人工修正对话框：原因必填（CNT-S-010）。计数模式可调 Delta；记录纠错模式隐藏 Delta。</summary>
public partial class CorrectionDialog : Window
{
    public long Delta { get; private set; }
    public string Reason => ReasonText.Text.Trim();
    public string OperatorName => AppServices.Instance.Settings.OperatorName;

    public CorrectionDialog(string prompt)
    {
        InitializeComponent();
        PromptText.Text = prompt;
    }

    /// <summary>记录纠错模式：无 Delta 输入。</summary>
    public CorrectionDialog(string prompt, bool hideDelta) : this(prompt)
    {
        if (hideDelta)
        {
            DeltaPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DeltaPanel.Visibility == Visibility.Visible)
        {
            if (!long.TryParse(DeltaText.Text.Trim(), out var delta) || delta == 0)
            {
                ThemedMessageBox.Show(this, "调整量必须是非零整数", "校验失败");
                return;
            }
            Delta = delta;
        }
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ThemedMessageBox.Show(this, "修正原因必填（CNT-S-010）", "校验失败");
            return;
        }
        DialogResult = true;
    }
}
