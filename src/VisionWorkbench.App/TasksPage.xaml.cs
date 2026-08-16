using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using VisionWorkbench.Application;
using VisionWorkbench.Domain;

namespace VisionWorkbench.App;

/// <summary>任务配置页（文档 §8.3）：配方 CRUD、规则编辑、ROI。</summary>
public partial class TasksPage : UserControl
{
    private readonly ObservableCollection<RuleRow> _rules = [];
    private long? _editingId;

    private sealed record TaskRow(long Id, string Name)
    {
        public long Id { get; } = Id;
        public string Name { get; } = Name;
    }

    public sealed class RuleRow
    {
        public string RuleId { get; set; } = "rule-1";
        public string Kind { get; set; } = nameof(RuleKind.CountEquals);
        public string? ClassId { get; set; }
        public long ExpectedCount { get; set; }
        public double MinConfidence { get; set; } = 0.6;
        public bool Enabled { get; set; } = true;
        public bool DowngradeToReview { get; set; }
    }

    public TasksPage()
    {
        InitializeComponent();
        RulesGrid.ItemsSource = _rules;
        Loaded += (_, _) => Refresh();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private async void Refresh()
    {
        try
        {
            var tasks = await AppServices.Instance.Recipes.ListAsync();
            TaskList.ItemsSource = tasks.Select(t => new TaskRow(t.Entity.Id, t.Recipe.Name)).ToArray();
            PluginCombo.ItemsSource = AppServices.Instance.AlgorithmManager.ScanPlugins()
                .Where(p => p.Status == Contracts.Plugins.PluginStatus.Valid && p.Manifest is not null)
                .Select(p => p.Manifest!.Id)
                .ToArray();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"刷新失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskRow row)
        {
            return;
        }
        var found = await AppServices.Instance.Recipes.FindAsync(row.Id);
        if (found is not { } pair)
        {
            return;
        }
        var (entity, recipe) = pair;
        _editingId = entity.Id;
        NameText.Text = recipe.Name;
        DescriptionText.Text = recipe.Description;
        ProviderText.Text = recipe.CameraProviderId;
        DeviceText.Text = recipe.CameraDeviceId;
        PluginCombo.SelectedItem = recipe.PluginId;
        if (PluginCombo.SelectedItem is null)
        {
            PluginCombo.Text = recipe.PluginId; // 插件不在列表时仍显示
        }
        SettingsText.Text = recipe.SettingsJson;
        RoiXText.Text = recipe.Roi?.X.ToString("0.###") ?? "0";
        RoiYText.Text = recipe.Roi?.Y.ToString("0.###") ?? "0";
        RoiWText.Text = recipe.Roi is { } r && r.Width > 0 ? r.Width.ToString("0.###") : "0";
        RoiHText.Text = recipe.Roi is { } r2 && r2.Height > 0 ? r2.Height.ToString("0.###") : "0";
        foreach (var item in RoiPolicyCombo.Items.OfType<ComboBoxItem>()
                     .Where(i => (string)i.Tag == recipe.RoiPolicy.ToString()))
        {
            RoiPolicyCombo.SelectedItem = item;
        }
        _rules.Clear();
        foreach (var rule in recipe.Rules)
        {
            _rules.Add(new RuleRow
            {
                RuleId = rule.RuleId,
                Kind = rule.Kind.ToString(),
                ClassId = rule.ClassId,
                ExpectedCount = rule.ExpectedCount,
                MinConfidence = rule.MinConfidence,
                Enabled = rule.Enabled,
                DowngradeToReview = rule.DowngradeToReview,
            });
        }
        EditorPanel.IsEnabled = true;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        TaskList.SelectedItem = null;
        _editingId = null;
        NameText.Text = "新任务";
        DescriptionText.Text = "";
        ProviderText.Text = "image-folder";
        DeviceText.Text = "";
        PluginCombo.SelectedIndex = PluginCombo.Items.Count > 0 ? 0 : -1;
        SettingsText.Text = "{}";
        RoiXText.Text = RoiYText.Text = RoiWText.Text = RoiHText.Text = "0";
        RoiPolicyCombo.SelectedIndex = 0;
        _rules.Clear();
        _rules.Add(new RuleRow { RuleId = "count-check", Kind = nameof(RuleKind.CountEquals), ExpectedCount = 1 });
        EditorPanel.IsEnabled = true;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskRow row)
        {
            return;
        }
        if (MessageBox.Show($"删除任务「{row.Name}」？", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
        {
            return;
        }
        await AppServices.Instance.Recipes.DeleteAsync(row.Id);
        Refresh();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(RoiXText.Text, out var roiX) || !double.TryParse(RoiYText.Text, out var roiY)
            || !double.TryParse(RoiWText.Text, out var roiW) || !double.TryParse(RoiHText.Text, out var roiH))
        {
            MessageBox.Show("ROI 必须是数字", "校验失败");
            return;
        }
        Contracts.Results.NormalizedRect? roi =
            roiW > 0 && roiH > 0 ? new() { X = roiX, Y = roiY, Width = roiW, Height = roiH } : null;

        var rules = _rules.Where(r => !string.IsNullOrWhiteSpace(r.RuleId)).Select(r => new InspectionRule
        {
            RuleId = r.RuleId.Trim(),
            Kind = Enum.TryParse<RuleKind>(r.Kind, out var kind) ? kind : RuleKind.CountEquals,
            ClassId = string.IsNullOrWhiteSpace(r.ClassId) ? null : r.ClassId.Trim(),
            ExpectedCount = r.ExpectedCount,
            MinConfidence = r.MinConfidence,
            Enabled = r.Enabled,
            DowngradeToReview = r.DowngradeToReview,
        }).ToArray();

        var policy = (RoiPolicyCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "CenterInside";
        var recipe = new Recipe
        {
            Name = NameText.Text.Trim(),
            Description = DescriptionText.Text,
            CameraProviderId = ProviderText.Text.Trim(),
            CameraDeviceId = DeviceText.Text.Trim(),
            PluginId = PluginCombo.SelectedItem as string ?? PluginCombo.Text.Trim(),
            SettingsJson = SettingsText.Text,
            Roi = roi,
            RoiPolicy = Enum.TryParse<RoiBoundaryPolicy>(policy, out var p) ? p : RoiBoundaryPolicy.CenterInside,
            Rules = rules,
        };
        try
        {
            await AppServices.Instance.Recipes.SaveAsync(recipe, _editingId);
            MessageBox.Show("已保存", "任务配置");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {ex.Message}", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
