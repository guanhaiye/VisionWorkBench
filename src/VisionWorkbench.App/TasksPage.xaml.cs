using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
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

    private sealed record TaskRow(long Id, string StationCode, string TaskName)
    {
        public long Id { get; } = Id;
        public string Name { get; } = $"[{StationCode}] {TaskName}";
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
            TaskList.ItemsSource = tasks.Select(t => new TaskRow(t.Entity.Id, t.Recipe.StationCode, t.Recipe.Name)).ToArray();
            PluginCombo.ItemsSource = AppServices.Instance.AlgorithmManager.ScanPlugins()
                .Where(p => p.Status == Contracts.Plugins.PluginStatus.Valid && p.Manifest is not null)
                .Select(p => p.Manifest!.Id)
                .ToArray();
            ApplyRolePolicy();
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
        StationCodeText.Text = recipe.StationCode;
        NameText.Text = recipe.Name;
        DescriptionText.Text = recipe.Description;
        ProviderText.Text = recipe.CameraProviderId;
        DeviceText.Text = recipe.CameraDeviceId;
        UpdateDevicePreview();
        PluginCombo.SelectedItem = recipe.PluginId;
        if (PluginCombo.SelectedItem is null)
        {
            PluginCombo.Text = recipe.PluginId; // 插件不在列表时仍显示
        }
        SettingsText.Text = recipe.SettingsJson;
        LoadYoloSettings();
        RoiXText.Text = recipe.Roi?.X.ToString("0.###") ?? "0";
        RoiYText.Text = recipe.Roi?.Y.ToString("0.###") ?? "0";
        RoiWText.Text = recipe.Roi is { } r && r.Width > 0 ? r.Width.ToString("0.###") : "0";
        RoiHText.Text = recipe.Roi is { } r2 && r2.Height > 0 ? r2.Height.ToString("0.###") : "0";
        foreach (var item in RoiPolicyCombo.Items.OfType<ComboBoxItem>()
                     .Where(i => (string)i.Tag == recipe.RoiPolicy.ToString()))
        {
            RoiPolicyCombo.SelectedItem = item;
        }
        SelectMode(recipe.CountingMode);
        LineAxText.Text = (recipe.CountingLine?.A.X ?? 0.5).ToString("0.###");
        LineAyText.Text = (recipe.CountingLine?.A.Y ?? 0.1).ToString("0.###");
        LineBxText.Text = (recipe.CountingLine?.B.X ?? 0.5).ToString("0.###");
        LineByText.Text = (recipe.CountingLine?.B.Y ?? 0.9).ToString("0.###");
        HysteresisText.Text = (recipe.CountingLine?.Hysteresis ?? 0.02).ToString("0.###");
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
        ApplyRolePolicy();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe)
        {
            MessageBox.Show("操作员模式不能修改任务配置，请切换到工程师或专家模式。", "权限限制");
            return;
        }
        TaskList.SelectedItem = null;
        _editingId = null;
        StationCodeText.Text = NextStationCode();
        NameText.Text = "新任务";
        DescriptionText.Text = "";
        ProviderText.Text = "image-folder";
        DeviceText.Text = "";
        UpdateDevicePreview();
        PluginCombo.SelectedIndex = PluginCombo.Items.Count > 0 ? 0 : -1;
        SettingsText.Text = "{}";
        LoadYoloSettings();
        RoiXText.Text = RoiYText.Text = RoiWText.Text = RoiHText.Text = "0";
        RoiPolicyCombo.SelectedIndex = 0;
        SelectMode(CountingMode.Snapshot);
        LineAxText.Text = "0.5";
        LineAyText.Text = "0.1";
        LineBxText.Text = "0.5";
        LineByText.Text = "0.9";
        HysteresisText.Text = "0.02";
        _rules.Clear();
        _rules.Add(new RuleRow { RuleId = "count-check", Kind = nameof(RuleKind.CountEquals), ExpectedCount = 1 });
        EditorPanel.IsEnabled = true;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe)
        {
            MessageBox.Show("操作员模式不能删除任务。", "权限限制");
            return;
        }
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
        if (!RolePolicy.CanEditRecipe)
        {
            MessageBox.Show("操作员模式不能保存任务配置。", "权限限制");
            return;
        }
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
        var mode = SelectedCountingMode();
        CountingLineConfig? line = null;
        if (mode == CountingMode.LineCrossing)
        {
            if (!double.TryParse(LineAxText.Text, out var ax) || !double.TryParse(LineAyText.Text, out var ay)
                || !double.TryParse(LineBxText.Text, out var bx) || !double.TryParse(LineByText.Text, out var by)
                || !double.TryParse(HysteresisText.Text, out var hysteresis))
            {
                MessageBox.Show("检测线参数必须是数字", "校验失败");
                return;
            }
            line = new CountingLineConfig
            {
                A = new() { X = ax, Y = ay },
                B = new() { X = bx, Y = by },
                Hysteresis = hysteresis,
            };
        }
        string settingsJson;
        try
        {
            settingsJson = BuildSettingsJson();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"模型/插件参数无效：{ex.Message}", "配置校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var recipe = new Recipe
        {
            StationCode = StationCodeText.Text.Trim(),
            Name = NameText.Text.Trim(),
            Description = DescriptionText.Text,
            CameraProviderId = ProviderText.Text.Trim(),
            CameraDeviceId = DeviceText.Text.Trim(),
            PluginId = PluginCombo.SelectedItem as string ?? PluginCombo.Text.Trim(),
            SettingsJson = settingsJson,
            Roi = roi,
            RoiPolicy = Enum.TryParse<RoiBoundaryPolicy>(policy, out var p) ? p : RoiBoundaryPolicy.CenterInside,
            CountingMode = mode,
            CountingLine = line,
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

    private string NextStationCode()
    {
        var used = TaskList.Items.OfType<TaskRow>()
            .Select(x => x.StationCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; ; number++)
        {
            var candidate = $"ST-{number:000}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LinePanel is null)
        {
            return; // XAML 初始化期回调，控件尚未就绪
        }
        LinePanel.IsEnabled = SelectedCountingMode() == CountingMode.LineCrossing;
    }

    private void BrowseDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe)
        {
            return;
        }
        var dialog = new OpenFolderDialog
        {
            Title = "选择离线图片目录",
            Multiselect = false,
        };
        if (Directory.Exists(DeviceText.Text))
        {
            dialog.InitialDirectory = DeviceText.Text;
        }
        if (dialog.ShowDialog() == true)
        {
            DeviceText.Text = dialog.FolderName;
            UpdateDevicePreview();
        }
    }

    private void InputSourceTextChanged(object sender, TextChangedEventArgs e) => UpdateDevicePreview();

    private void PluginCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelGroup is null)
        {
            return;
        }
        LoadYoloSettings();
    }

    private void YoloTaskCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (YoloTaskCombo is null || ModelCombo is null)
        {
            return;
        }
        LoadModelChoices();
    }

    private void ImportModel_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe)
        {
            MessageBox.Show("当前角色无权导入模型，请切换到工程师或专家模式。", "权限限制");
            return;
        }
        var plugin = SelectedPlugin();
        if (plugin is null)
        {
            MessageBox.Show("请先选择有效的 YOLO11 插件。", "模型导入");
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "选择 YOLO 模型文件",
            Filter = "模型文件 (*.pt;*.onnx)|*.pt;*.onnx|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var modelsDirectory = Path.Combine(plugin.Directory, "models");
            Directory.CreateDirectory(modelsDirectory);
            var fileName = Path.GetFileName(dialog.FileName);
            var destination = Path.Combine(modelsDirectory, fileName);
            File.Copy(dialog.FileName, destination, overwrite: true);
            var relative = $"models/{fileName}";
            if (!ModelCombo.Items.Contains(relative))
            {
                ModelCombo.Items.Add(relative);
            }
            ModelCombo.SelectedItem = relative;
            ModelStatusText.Text = $"已导入：{relative}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"模型导入失败：{ex.Message}", "模型导入", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateDevicePreview()
    {
        if (DevicePreviewText is null)
        {
            return;
        }
        if (!string.Equals(ProviderText.Text.Trim(), "image-folder", StringComparison.OrdinalIgnoreCase))
        {
            DevicePreviewText.Text = "当前输入源不是离线图片目录";
            BrowseDeviceButton.IsEnabled = false;
            return;
        }
        BrowseDeviceButton.IsEnabled = RolePolicy.CanEditRecipe;
        var directory = DeviceText.Text.Trim();
        if (!Directory.Exists(directory))
        {
            DevicePreviewText.Text = "目录不存在，请选择有效的图片目录";
            return;
        }
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp" };
        var count = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Count(file => extensions.Contains(Path.GetExtension(file)));
        DevicePreviewText.Text = $"目录有效：{count} 张图片（仅读取当前目录，不递归子目录）";
    }

    private bool IsYoloPlugin => string.Equals(
        (PluginCombo.SelectedItem as string) ?? PluginCombo.Text,
        "com.vision.yolo11", StringComparison.OrdinalIgnoreCase);

    private Contracts.Plugins.DiscoveredPlugin? SelectedPlugin() =>
        AppServices.Instance.AlgorithmManager.ScanPlugins().FirstOrDefault(p =>
            p.Status == Contracts.Plugins.PluginStatus.Valid
            && string.Equals(p.Manifest?.Id, (PluginCombo.SelectedItem as string) ?? PluginCombo.Text,
                StringComparison.OrdinalIgnoreCase));

    private void LoadYoloSettings()
    {
        if (ModelGroup is null)
        {
            return;
        }
        ModelGroup.Visibility = IsYoloPlugin ? Visibility.Visible : Visibility.Collapsed;
        if (!IsYoloPlugin)
        {
            return;
        }
        try
        {
            var settings = JsonNode.Parse(string.IsNullOrWhiteSpace(SettingsText.Text) ? "{}" : SettingsText.Text)
                as JsonObject;
            var task = settings?["task"]?.GetValue<string>() ?? "detect";
            YoloTaskCombo.SelectedItem = YoloTaskCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, task, StringComparison.OrdinalIgnoreCase));
            LoadModelChoices();
            var modelPath = settings?["modelPath"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                if (!ModelCombo.Items.Contains(modelPath))
                {
                    ModelCombo.Items.Add(modelPath);
                }
                ModelCombo.SelectedItem = modelPath;
            }
        }
        catch
        {
            ModelStatusText.Text = "专家参数不是有效 JSON，请检查后再保存";
        }
    }

    private void LoadModelChoices()
    {
        if (!IsYoloPlugin)
        {
            return;
        }
        var selected = ModelCombo.SelectedItem as string;
        ModelCombo.Items.Clear();
        var plugin = SelectedPlugin();
        if (plugin is not null)
        {
            var modelsDirectory = Path.Combine(plugin.Directory, "models");
            if (Directory.Exists(modelsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(modelsDirectory)
                             .Where(file => Path.GetExtension(file).Equals(".pt", StringComparison.OrdinalIgnoreCase)
                                 || Path.GetExtension(file).Equals(".onnx", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
                {
                    ModelCombo.Items.Add($"models/{Path.GetFileName(file)}");
                }
            }
        }
        if (selected is not null && ModelCombo.Items.Contains(selected))
        {
            ModelCombo.SelectedItem = selected;
        }
        else if (ModelCombo.Items.Count > 0)
        {
            ModelCombo.SelectedIndex = 0;
        }
        ModelStatusText.Text = ModelCombo.SelectedItem is string path
            ? $"可用模型：{path}"
            : "尚未导入模型文件";
    }

    private string BuildSettingsJson()
    {
        if (!IsYoloPlugin)
        {
            return SettingsText.Text;
        }
        var settings = JsonNode.Parse(string.IsNullOrWhiteSpace(SettingsText.Text) ? "{}" : SettingsText.Text)
            as JsonObject ?? new JsonObject();
        var task = (YoloTaskCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "detect";
        settings["task"] = task;
        if (ModelCombo.SelectedItem is string modelPath && !string.IsNullOrWhiteSpace(modelPath))
        {
            settings["modelPath"] = modelPath;
        }
        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private void ApplyRolePolicy()
    {
        var canEdit = RolePolicy.CanEditRecipe;
        EditorPanel.IsEnabled = canEdit;
        AdvancedSettingsGroup.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
        SettingsText.IsEnabled = RolePolicy.CanEditAdvanced;
        AdvancedHintText.Text = RolePolicy.CanEditAdvanced
            ? "专家可直接编辑插件 JSON；YOLO11 的任务类型和模型文件可在上方配置。"
            : "当前角色只能查看专家参数；如需修改请切换到专家模式。";
        SaveButton.IsEnabled = canEdit;
        BrowseDeviceButton.IsEnabled = canEdit;
        ImportModelButton.IsEnabled = canEdit;
        UpdateDevicePreview();
    }

    private CountingMode SelectedCountingMode()
    {
        var tag = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<CountingMode>(tag, out var mode) ? mode : CountingMode.Snapshot;
    }

    private void SelectMode(CountingMode mode)
    {
        foreach (var item in ModeCombo.Items.OfType<ComboBoxItem>()
                     .Where(i => (string)i.Tag == mode.ToString()))
        {
            ModeCombo.SelectedItem = item;
        }
        if (LinePanel is not null)
        {
            LinePanel.IsEnabled = mode == CountingMode.LineCrossing;
        }
    }
}
