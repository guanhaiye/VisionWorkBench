using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VisionWorkbench.Domain;

namespace VisionWorkbench.App;

public sealed record RuleClassOption(string ClassId, string DisplayName);

public partial class RuleAddDialog : Window
{
    public RuleKind Kind { get; private set; } = RuleKind.AreaRange;
    public string? ClassId { get; private set; }
    public double Minimum { get; private set; }
    public double Maximum { get; private set; }

    public RuleAddDialog(IReadOnlyList<RuleClassOption>? classOptions = null)
    {
        InitializeComponent();
        ClassCombo.ItemsSource = (classOptions is { Count: > 0 } ? classOptions :
            [new RuleClassOption("", "所有类别")]);
        KindCombo.SelectionChanged += KindCombo_SelectionChanged;
    }

    private void KindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HintText is null)
        {
            return;
        }
        HintText.Text = SelectedKind() == RuleKind.CountRange
            ? "数量必须为整数；留空表示对应方向不限。"
            : "面积单位为像素²，直径单位为像素；留空表示对应方向不限。";
    }

    private RuleKind SelectedKind()
    {
        var tag = (KindCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<RuleKind>(tag, out var kind) ? kind : RuleKind.AreaRange;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var kind = SelectedKind();
        if (!TryParseOptional(MinimumText.Text, out var minimum)
            || !TryParseOptional(MaximumText.Text, out var maximum))
        {
            ShowValidation("最小值和最大值必须是数字，留空表示不限。");
            return;
        }
        if (minimum <= 0 && maximum <= 0)
        {
            ShowValidation("最小值和最大值至少填写一个。");
            return;
        }
        if (minimum < 0 || maximum < 0 || (maximum > 0 && minimum > 0 && minimum > maximum))
        {
            ShowValidation("范围必须为非负数，且最小值不能大于最大值。");
            return;
        }
        if (kind == RuleKind.CountRange
            && (!IsInteger(minimum) || !IsInteger(maximum)))
        {
            ShowValidation("数量范围必须填写整数。");
            return;
        }

        Kind = kind;
        ClassId = ClassCombo.SelectedValue as string;
        if (string.IsNullOrWhiteSpace(ClassId))
        {
            ClassId = null;
        }
        Minimum = minimum;
        Maximum = maximum;
        DialogResult = true;
    }

    private static bool TryParseOptional(string text, out double value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            return true;
        }
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsInteger(double value) => value <= 0 || Math.Abs(value - Math.Round(value)) < 0.000001;

    private void ShowValidation(string message)
    {
        ThemedMessageBox.Show(message, "规则设置", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
