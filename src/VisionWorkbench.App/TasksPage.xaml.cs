using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Application;
using VisionWorkbench.Domain;

namespace VisionWorkbench.App;

/// <summary>任务配置页（文档 §8.3）：配方 CRUD、规则编辑、ROI。</summary>
public partial class TasksPage : UserControl
{
    private readonly ObservableCollection<RuleRow> _rules = [];
    private long? _editingId;
    private string? _lastInputProviderId;
    private bool _suppressInputSourceChange;
    private readonly PreviewRenderer _cameraPreview = new();
    private readonly SemaphoreSlim _cameraPreviewGate = new(1, 1);
    private ICameraSession? _cameraPreviewSession;
    private CancellationTokenSource? _cameraPreviewCts;
    private readonly Dictionary<string, CheckBox> _customBehaviorChecks = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _customBehaviorClasses = [];
    private string _settingsJson = "{}";
    private PostProcessMode _postProcessMode = PostProcessMode.VisualRules;
    private string _postProcessScript = "";
    private bool _syncingSemanticPlugin;
    private IReadOnlyList<SopDefinition> _sopDefinitions = [];

    private sealed record InputSourceOption(string DisplayName, string ProviderId, string DeviceId)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record SopOption(SopDefinition Definition)
    {
        public string DisplayName => $"{Definition.Name} · v{Definition.Version}";
    }

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
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public bool Enabled { get; set; } = true;
        public bool DowngradeToReview { get; set; }
    }

    public TasksPage()
    {
        InitializeComponent();
        if (!YoloTaskCombo.Items.OfType<ComboBoxItem>().Any(item => string.Equals(item.Tag as string, "pose", StringComparison.OrdinalIgnoreCase)))
        {
            YoloTaskCombo.Items.Add(new ComboBoxItem { Content = "人体姿态（Pose）", Tag = "pose" });
        }
        RulesGrid.ItemsSource = _rules;
        UpdatePostProcessUi();
        Loaded += (_, _) => Refresh();
        Unloaded += TasksPage_Unloaded;
    }

    private async void TasksPage_Unloaded(object sender, RoutedEventArgs e)
    {
        await StopCameraPreviewAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private async void Refresh()
    {
        try
        {
            var tasks = await AppServices.Instance.Recipes.ListAsync();
            TaskList.ItemsSource = tasks
                .Select(t => new TaskRow(t.Entity.Id, t.Recipe.StationCode, t.Recipe.Name))
                .ToArray();
            _sopDefinitions = (await AppServices.Instance.SopDefinitions.ListAsync())
                .Where(definition => definition.Status == SopDefinitionStatus.Published)
                .OrderByDescending(definition => definition.Version)
                .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SopCombo.ItemsSource = _sopDefinitions.Select(definition => new SopOption(definition)).ToArray();
            PluginCombo.ItemsSource = AppServices.Instance.AlgorithmManager.ScanPlugins()
                .Where(p => p.Status == Contracts.Plugins.PluginStatus.Valid && p.Manifest is not null)
                .Select(p => p.Manifest!.Id)
                .ToArray();
            await RefreshInputSourcesAsync();
            ApplyRolePolicy();
            UpdateTaskTypeVisibility();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"刷新失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (recipe.Sop is not null)
        {
            SelectSopTaskType();
            SelectSopDefinition(recipe.Sop);
        }
        else
        {
            SelectTaskType(recipe.TaskType.IsCounting()
                && recipe.Behavior.Enabled
                && recipe.Behavior.Zones.Count > 0
                ? InspectionTaskType.BehaviorRecognition
                : recipe.TaskType);
        }
        DeviceText.Text = recipe.CameraDeviceId;
        SelectInputSource(recipe.CameraProviderId, recipe.CameraDeviceId);
        UpdateDevicePreview();
        PluginCombo.SelectedItem = recipe.PluginId;
        if (PluginCombo.SelectedItem is null)
        {
            PluginCombo.Text = recipe.PluginId; // 插件不在列表时仍显示
        }
        SelectExecutionProvider(recipe.ExecutionProvider);
        _settingsJson = string.IsNullOrWhiteSpace(recipe.SettingsJson) ? "{}" : recipe.SettingsJson;
        _postProcessMode = recipe.PostProcess.Mode;
        _postProcessScript = recipe.PostProcess.Script ?? "";
        SelectPostProcessMode(_postProcessMode);
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
        LoadBehavior(recipe.Behavior);
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
                Minimum = rule.Minimum,
                Maximum = rule.Maximum,
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
            ThemedMessageBox.Show("操作员模式不能修改任务配置，请切换到工程师或专家模式。", "权限限制");
            return;
        }
        TaskList.SelectedItem = null;
        _editingId = null;
        StationCodeText.Text = NextStationCode();
        NameText.Text = NextTaskName();
        DescriptionText.Text = "";
        SelectTaskType(null);
        SopCombo.SelectedIndex = -1;
        DeviceText.Text = "";
        SelectInputSource("image-folder", "");
        UpdateDevicePreview();
        PluginCombo.SelectedIndex = PluginCombo.Items.Count > 0 ? 0 : -1;
        SelectExecutionProvider(AppServices.Instance.Settings.ExecutionProvider);
        _settingsJson = "{}";
        _postProcessMode = PostProcessMode.VisualRules;
        _postProcessScript = "";
        SelectPostProcessMode(_postProcessMode);
        LoadYoloSettings();
        RoiXText.Text = RoiYText.Text = RoiWText.Text = RoiHText.Text = "0";
        RoiPolicyCombo.SelectedIndex = 0;
        SelectMode(CountingMode.Snapshot);
        LineAxText.Text = "0.5";
        LineAyText.Text = "0.1";
        LineBxText.Text = "0.5";
        LineByText.Text = "0.9";
        HysteresisText.Text = "0.02";
        LoadBehavior(new BehaviorRecognitionConfig());
        _rules.Clear();
        _rules.Add(new RuleRow { RuleId = "count-check", Kind = nameof(RuleKind.CountEquals), ExpectedCount = 1 });
        EditorPanel.IsEnabled = true;
        TaskEditorScrollViewer.ScrollToTop();
        NameText.Focus();
        NameText.SelectAll();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe)
        {
            ThemedMessageBox.Show("操作员模式不能删除任务。", "权限限制");
            return;
        }
        if (TaskList.SelectedItem is not TaskRow row)
        {
            return;
        }
        if (ThemedMessageBox.Show($"删除任务「{row.Name}」？", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
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
            ThemedMessageBox.Show("操作员模式不能保存任务配置。", "权限限制");
            return;
        }
        if (!double.TryParse(RoiXText.Text, out var roiX) || !double.TryParse(RoiYText.Text, out var roiY)
            || !double.TryParse(RoiWText.Text, out var roiW) || !double.TryParse(RoiHText.Text, out var roiH))
        {
            ThemedMessageBox.Show("ROI 必须是数字", "校验失败");
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
            Minimum = r.Minimum,
            Maximum = r.Maximum,
            Enabled = r.Enabled,
            DowngradeToReview = r.DowngradeToReview,
        }).ToArray();

        if (!HasTaskTypeSelection())
        {
            ThemedMessageBox.Show("请先选择任务类型，再配置对应参数。", "任务配置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var isSopTask = IsSopTask();
        var taskType = SelectedTaskType();
        _postProcessMode = SelectedPostProcessMode();
        if (!isSopTask && _postProcessMode == PostProcessMode.PythonScript && string.IsNullOrWhiteSpace(_postProcessScript))
        {
            ThemedMessageBox.Show("请先点击“编辑 Python 脚本”并填写后处理脚本。", "配置校验失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var policy = (RoiPolicyCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "CenterInside";
        var mode = SelectedCountingMode();
        CountingLineConfig? line = null;
        if (mode == CountingMode.LineCrossing)
        {
            if (!double.TryParse(LineAxText.Text, out var ax) || !double.TryParse(LineAyText.Text, out var ay)
                || !double.TryParse(LineBxText.Text, out var bx) || !double.TryParse(LineByText.Text, out var by)
                || !double.TryParse(HysteresisText.Text, out var hysteresis))
            {
                ThemedMessageBox.Show("检测线参数必须是数字", "校验失败");
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
        BehaviorRecognitionConfig behavior;
        SopBinding? sop = null;
        string pluginId;
        string executionProvider;
        if (isSopTask)
        {
            if (SopCombo.SelectedItem is not SopOption sopOption)
            {
                ThemedMessageBox.Show("请选择一个已发布的SOP流程。", "任务配置", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var definition = sopOption.Definition;
            var stepExecution = GetFirstSopStepExecution(definition);
            if (stepExecution is null || string.IsNullOrWhiteSpace(stepExecution.PluginId))
            {
                ThemedMessageBox.Show("所选SOP没有完整的工序模型配置，请返回SOP页面补充后再发布。", "任务配置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var execution = definition.Execution;
            sop = new SopBinding
            {
                DefinitionId = definition.Id,
                Version = definition.Version,
                RunMode = execution?.RunMode ?? SopRunMode.StrictOrder,
                Definition = definition,
            };
            taskType = stepExecution.TaskType;
            pluginId = stepExecution.PluginId;
            executionProvider = NormalizeExecutionProvider(stepExecution.ExecutionProvider);
            settingsJson = string.IsNullOrWhiteSpace(stepExecution.SettingsJson) ? "{}" : stepExecution.SettingsJson;
            behavior = execution?.Behavior ?? new BehaviorRecognitionConfig();
            roi = stepExecution.Roi ?? execution?.Roi;
            policy = stepExecution.Roi is not null
                ? stepExecution.RoiPolicy.ToString()
                : execution?.RoiPolicy.ToString() ?? "CenterInside";
            mode = execution?.CountingMode ?? CountingMode.Snapshot;
            line = execution?.CountingLine;
            _postProcessMode = execution?.PostProcess.Mode ?? PostProcessMode.VisualRules;
            _postProcessScript = execution?.PostProcess.Script ?? "";
        }
        else
        {
            try
            {
                settingsJson = BuildSettingsJson();
                _settingsJson = settingsJson;
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show($"模型/插件参数无效：{ex.Message}", "配置校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                behavior = BuildBehavior();
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show($"行为识别配置无效：{ex.Message}", "配置校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            pluginId = PluginCombo.SelectedItem as string ?? PluginCombo.Text.Trim();
            executionProvider = SelectedExecutionProvider();
        }

        var recipe = new Recipe
        {
            StationCode = StationCodeText.Text.Trim(),
            Name = NameText.Text.Trim(),
            Description = DescriptionText.Text,
            CameraProviderId = SelectedProviderId(),
            CameraDeviceId = SelectedDeviceId(),
            PluginId = pluginId,
            ExecutionProvider = executionProvider,
            TaskType = taskType,
            SettingsJson = settingsJson,
            Roi = roi,
            RoiPolicy = Enum.TryParse<RoiBoundaryPolicy>(policy, out var p) ? p : RoiBoundaryPolicy.CenterInside,
            CountingMode = taskType.IsRegion() ? CountingMode.Snapshot : mode,
            CountingLine = taskType.IsRegion() ? null : line,
            Behavior = behavior,
            PostProcess = new PostProcessConfig
            {
                Mode = _postProcessMode,
                Script = _postProcessScript.Trim(),
            },
            Sop = sop,
            Rules = isSopTask ? [] : taskType.IsCounting() || taskType.IsRegion() ? rules : [],
        };
        try
        {
            await AppServices.Instance.Recipes.SaveAsync(recipe, _editingId);
            ThemedMessageBox.Show("已保存", "任务配置");
            Refresh();
        }
        catch (Exception ex)
        {
            var detail = ex is DbUpdateException && ex.InnerException is not null
                ? ex.InnerException.Message
                : ex.Message;
            ThemedMessageBox.Show($"保存失败：{detail}", "任务配置", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private string NextTaskName()
    {
        var used = TaskList.Items.OfType<TaskRow>()
            .Select(x => x.TaskName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        const string baseName = "新任务";
        if (!used.Contains(baseName))
        {
            return baseName;
        }

        for (var number = 2; ; number++)
        {
            var candidate = $"{baseName} {number}";
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

    private void TaskTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTaskTypeVisibility();
    }

    private void SopCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsSopTask() && SopCombo.SelectedItem is SopOption option)
        {
            SopBindingHintText.Text = $"已选择：{option.Definition.Name} v{option.Definition.Version}。保存任务后，该任务即可在“实时检测”中选择。";
        }
    }

    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe)
        {
            return;
        }

        var dialog = new RuleAddDialog(await ReadRuleClassOptionsAsync())
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var prefix = dialog.Kind switch
        {
            RuleKind.AreaRange => "area",
            RuleKind.DiameterRange => "diameter",
            RuleKind.CountRange => "count",
            _ => "rule",
        };
        var number = 1;
        string ruleId;
        do
        {
            ruleId = $"{prefix}-{number++}";
        }
        while (_rules.Any(rule => string.Equals(rule.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)));

        var row = new RuleRow
        {
            RuleId = ruleId,
            Kind = dialog.Kind.ToString(),
            ClassId = dialog.ClassId,
            Minimum = dialog.Minimum,
            Maximum = dialog.Maximum,
            Enabled = true,
        };
        _rules.Add(row);
        RulesGrid.ScrollIntoView(row);
        RulesGrid.SelectedItem = row;
    }

    private async Task<IReadOnlyList<RuleClassOption>> ReadRuleClassOptionsAsync()
    {
        var options = new List<RuleClassOption>
        {
            new("", "所有类别"),
        };
        if (ModelCombo.SelectedItem is not string modelPath || string.IsNullOrWhiteSpace(modelPath))
        {
            return options;
        }

        var candidates = new List<string>
        {
            Path.ChangeExtension(modelPath, ".json"),
            modelPath + ".meta.json",
            Path.ChangeExtension(modelPath, ".engine.meta.json"),
        };
        var directory = Path.GetDirectoryName(modelPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var current = new DirectoryInfo(directory);
            for (var level = 0; level < 3 && current is not null; level++, current = current.Parent)
            {
                candidates.Add(Path.Combine(current.FullName, "classes.json"));
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(candidate));
                var classNode = root is JsonObject obj
                    ? obj["classes"] ?? obj["names"]
                    : root;
                AddRuleClasses(classNode, options);
                if (options.Count > 1)
                {
                    break;
                }
            }
            catch
            {
                // Ignore an invalid sidecar and continue searching other supported metadata files.
            }
        }
        if (options.Count == 1)
        {
            var modelClasses = await ReadModelClassesAsync(modelPath);
            AddRuleClasses(modelClasses, options);
        }
        return options;
    }

    private async Task<JsonNode?> ReadModelClassesAsync(string modelPath)
    {
        var yoloPlugin = AppServices.Instance.AlgorithmManager.ScanPlugins()
            .FirstOrDefault(plugin => string.Equals(plugin.Manifest?.Id, "com.vision.yolo11", StringComparison.OrdinalIgnoreCase));
        if (yoloPlugin is null)
        {
            return null;
        }
        var helperPath = Path.Combine(yoloPlugin.Directory, "model_metadata.py");
        if (!File.Exists(helperPath))
        {
            return null;
        }

        var pluginPythonCandidates = new[]
        {
            Path.Combine(yoloPlugin.Directory, ".venv", "Scripts", "python.exe"),
            Path.Combine(yoloPlugin.Directory, "..", "atu5", ".venv", "Scripts", "python.exe"),
            Path.Combine(yoloPlugin.Directory, "..", ".venv", "Scripts", "python.exe"),
        };
        var python = pluginPythonCandidates.FirstOrDefault(File.Exists) ?? "python";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(helperPath);
        process.StartInfo.ArgumentList.Add(modelPath);
        try
        {
            if (!process.Start())
            {
                return null;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var jsonLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Reverse()
                .FirstOrDefault(line => line.TrimStart().StartsWith("{"));
            if (string.IsNullOrWhiteSpace(jsonLine))
            {
                return null;
            }
            var root = JsonNode.Parse(jsonLine);
            return root is JsonObject obj ? obj["classes"] ?? obj["names"] : root;
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            return null;
        }
    }

    private static void AddRuleClasses(JsonNode? node, ICollection<RuleClassOption> options)
    {
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var name = array[index]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(name)
                    || (index == 0 && string.Equals(name, "background", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                options.Add(new RuleClassOption(index.ToString(CultureInfo.InvariantCulture),
                    $"{name}（ID {index}）"));
            }
            return;
        }

        if (node is not JsonObject map)
        {
            return;
        }
        foreach (var pair in map)
        {
            if (pair.Value is null || !int.TryParse(pair.Key, out var id))
            {
                continue;
            }
            var name = pair.Value.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name)
                || (id == 0 && string.Equals(name, "background", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            options.Add(new RuleClassOption(id.ToString(CultureInfo.InvariantCulture),
                $"{name}（ID {id}）"));
        }
    }

    private bool HasTaskTypeSelection()
    {
        var tag = (TaskTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.Equals(tag, "Sop", StringComparison.OrdinalIgnoreCase)
            || Enum.TryParse<InspectionTaskType>(tag, out _);
    }

    private bool IsSopTask() => string.Equals(
        (TaskTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string,
        "Sop", StringComparison.OrdinalIgnoreCase);

    private InspectionTaskType SelectedTaskType()
    {
        var tag = (TaskTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<InspectionTaskType>(tag, out var type)
            ? type
            : InspectionTaskType.Detection;
    }

    private void SelectSopTaskType()
    {
        TaskTypeCombo.SelectedItem = TaskTypeCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "Sop", StringComparison.OrdinalIgnoreCase));
        UpdateTaskTypeVisibility();
    }

    private void SelectSopDefinition(SopBinding binding)
    {
        var option = SopCombo.Items.OfType<SopOption>()
            .FirstOrDefault(item => string.Equals(item.Definition.Id, binding.DefinitionId, StringComparison.Ordinal));
        if (option is null && binding.Definition is not null)
        {
            var definitions = SopCombo.Items.OfType<SopOption>().ToList();
            definitions.Insert(0, new SopOption(binding.Definition));
            SopCombo.ItemsSource = definitions;
            option = definitions[0];
        }
        SopCombo.SelectedItem = option;
    }

    private static SopStepExecution? GetFirstSopStepExecution(SopDefinition definition)
    {
        var stepExecution = definition.Steps
            .OrderBy(step => step.Order)
            .Select(step => step.Execution)
            .FirstOrDefault(execution => execution is not null);
        if (stepExecution is not null)
        {
            return stepExecution;
        }
        if (definition.Execution is not { } execution)
        {
            return null;
        }
        return new SopStepExecution
        {
            PluginId = execution.PluginId,
            TaskType = execution.TaskType,
            ExecutionProvider = execution.ExecutionProvider,
            SettingsJson = execution.SettingsJson,
            Roi = execution.Roi,
            RoiPolicy = execution.RoiPolicy,
            Rules = execution.Rules,
        };
    }

    private static string NormalizeExecutionProvider(string? provider) =>
        string.Equals(provider, "cuda", StringComparison.OrdinalIgnoreCase) ? "cuda" : "cpu";

    private void SelectTaskType(InspectionTaskType? type)
    {
        if (TaskTypeCombo is null)
        {
            return;
        }
        var tag = type?.ToString() ?? "";
        TaskTypeCombo.SelectedItem = TaskTypeCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? TaskTypeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        UpdateTaskTypeVisibility();
    }

    private void UpdateTaskTypeVisibility()
    {
        if (TaskTypeCombo is null || ModeCombo is null || RoiXText is null || RulesGrid is null || BehaviorGroup is null)
        {
            return;
        }
        var selectedType = SelectedTaskType();
        var isSop = IsSopTask();
        SopBindingGroup.Visibility = isSop ? Visibility.Visible : Visibility.Collapsed;
        AlgorithmGroup.Visibility = isSop ? Visibility.Collapsed : Visibility.Visible;
        if (isSop)
        {
            ModelGroup.Visibility = Visibility.Collapsed;
            BehaviorGroup.Visibility = Visibility.Collapsed;
            SetGroupVisibility(ModeCombo, false);
            SetGroupVisibility(RoiXText, false);
            SetGroupVisibility(RulesGrid, false);
            return;
        }
        ModelGroup.Visibility = IsYoloPlugin || IsAtu5Plugin ? Visibility.Visible : Visibility.Collapsed;
        var isCounting = HasTaskTypeSelection() && selectedType.IsCounting();
        var isRegion = HasTaskTypeSelection() && selectedType.IsRegion();
        var isBehavior = HasTaskTypeSelection() && selectedType.IsBehavior();
        var isSemantic = HasTaskTypeSelection()
            && selectedType.Normalize() == InspectionTaskType.SemanticSegmentation;
        if (isRegion && _rules.Count == 1
            && string.Equals(_rules[0].RuleId, "count-check", StringComparison.OrdinalIgnoreCase)
            && string.Equals(_rules[0].Kind, nameof(RuleKind.CountEquals), StringComparison.OrdinalIgnoreCase)
            && _rules[0].ExpectedCount == 1)
        {
            _rules.Clear();
        }
        else if (isCounting && _rules.Count == 0)
        {
            _rules.Add(new RuleRow { RuleId = "count-check", Kind = nameof(RuleKind.CountEquals), ExpectedCount = 1 });
        }
        EnsureSemanticPluginBinding(isSemantic);
        GeneralPluginSelectorPanel.Visibility = isSemantic ? Visibility.Collapsed : Visibility.Visible;
        SemanticPluginFixedText.Visibility = isSemantic ? Visibility.Visible : Visibility.Collapsed;
        if (isRegion && IsYoloPlugin)
        {
            var modelTask = selectedType.Normalize() == InspectionTaskType.SemanticSegmentation ? "semantic" : "instance";
            var modelTaskItem = YoloTaskCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, modelTask, StringComparison.OrdinalIgnoreCase));
            if (modelTaskItem is not null) YoloTaskCombo.SelectedItem = modelTaskItem;
        }
        YoloTaskCombo.IsEnabled = !isRegion;
        SetGroupVisibility(ModeCombo, isCounting);
        SetGroupVisibility(RoiXText, isCounting);
        SetGroupVisibility(RulesGrid, isCounting || isRegion);
        UpdateTaskParameterVisibility();
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
        UpdateTaskTypeVisibility();
    }

    private void YoloTaskCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (YoloTaskCombo is null || ModelCombo is null)
        {
            return;
        }
        LoadModelChoices();
        UpdateTaskParameterVisibility();
    }

    private async Task RefreshInputSourcesAsync()
    {
        var sources = new List<InputSourceOption>
        {
            new("离线图片目录（image-folder）", "image-folder", ""),
            new("视频文件（video-file）", "video-file", ""),
        };
        try
        {
            var cameras = await AppServices.Instance.Cameras.DiscoverAllAsync(CancellationToken.None);
            sources.AddRange(cameras
                .Select(camera => new InputSourceOption(
                    $"{camera.DisplayName}（{camera.ProviderId}/{camera.DeviceId}）",
                    camera.ProviderId,
                    camera.DeviceId))
                .DistinctBy(source => $"{source.ProviderId}/{source.DeviceId}", StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            // 相机扫描失败时保留离线输入源，避免任务配置页不可用。
        }
        ProviderCombo.ItemsSource = sources;
        if (ProviderCombo.SelectedItem is null)
        {
            ProviderCombo.SelectedIndex = 0;
        }
    }

    private void SelectInputSource(string providerId, string deviceId)
    {
        var options = ProviderCombo.ItemsSource is IEnumerable<InputSourceOption> existing
            ? existing.ToList()
            : [];
        var selected = options.FirstOrDefault(source =>
            string.Equals(source.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = new InputSourceOption(
                $"未发现设备（{providerId}/{deviceId}）", providerId, deviceId);
            options.Add(selected);
            ProviderCombo.ItemsSource = options;
        }
        _suppressInputSourceChange = true;
        try
        {
            ProviderCombo.SelectedItem = selected;
        }
        finally
        {
            _suppressInputSourceChange = false;
        }
        _lastInputProviderId = providerId;
        if (string.Equals(providerId, "usb", StringComparison.OrdinalIgnoreCase))
        {
            DeviceText.Text = deviceId;
        }
        UpdateInputSourceFields();
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInputSourceChange || ProviderCombo.SelectedItem is not InputSourceOption source)
        {
            return;
        }
        if (!IsPathInputSource(source.ProviderId))
        {
            DeviceText.Text = source.DeviceId;
        }
        else if (_lastInputProviderId is not null && !IsPathInputSource(_lastInputProviderId))
        {
            DeviceText.Text = "";
        }
        _lastInputProviderId = source.ProviderId;
        UpdateDevicePreview();
    }

    private string SelectedProviderId() =>
        (ProviderCombo.SelectedItem as InputSourceOption)?.ProviderId ?? "image-folder";

    private static bool IsPathInputSource(string providerId) =>
        string.Equals(providerId, "image-folder", StringComparison.OrdinalIgnoreCase)
        || string.Equals(providerId, "video-file", StringComparison.OrdinalIgnoreCase);

    private string SelectedDeviceId()
    {
        var source = ProviderCombo.SelectedItem as InputSourceOption;
        return source is not null && !IsPathInputSource(source.ProviderId)
            ? source.DeviceId
            : DeviceText.Text.Trim();
    }

    private void UpdateInputSourceFields()
    {
        var providerId = SelectedProviderId();
        var isPathSource = IsPathInputSource(providerId);
        DeviceText.Visibility = isPathSource ? Visibility.Visible : Visibility.Collapsed;
        BrowseDeviceButton.Visibility = string.Equals(providerId, "image-folder", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CameraPreviewPanel.Visibility = isPathSource ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RequestCameraPreviewRefresh() => _ = RefreshCameraPreviewAsync();

    private async Task RefreshCameraPreviewAsync()
    {
        var cts = new CancellationTokenSource();
        var previous = _cameraPreviewCts;
        _cameraPreviewCts = cts;
        previous?.Cancel();
        var cancellationToken = cts.Token;

        try
        {
            await _cameraPreviewGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            cts.Dispose();
            return;
        }

        ICameraSession? session = null;
        try
        {
            await DisposeCameraPreviewSessionAsync();
            var providerId = SelectedProviderId();
            if (IsPathInputSource(providerId) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var deviceId = SelectedDeviceId();
            CameraPreviewStatusText.Visibility = Visibility.Visible;
            CameraPreviewStatusText.Text = string.IsNullOrWhiteSpace(deviceId)
                ? "未选择相机"
                : "正在打开相机...";
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            var descriptor = new CameraDescriptor
            {
                ProviderId = providerId,
                DeviceId = deviceId,
                DisplayName = deviceId,
            };
            session = await AppServices.Instance.Cameras.OpenSessionAsync(
                descriptor, new CameraOpenOptions { DesiredFps = 15 }, cancellationToken);
            _cameraPreviewSession = session;
            session.Faulted += (_, args) => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_cameraPreviewSession, session))
                {
                    CameraPreviewStatusText.Visibility = Visibility.Visible;
                    CameraPreviewStatusText.Text = $"相机预览失败：{args.Fault.Message}";
                }
            });
            session.FrameReceived += (_, args) => Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_cameraPreviewSession, session) || cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                _cameraPreview.Render(CameraPreviewImage, args.Frame, maxFps: 15);
                CameraPreviewStatusText.Visibility = Visibility.Collapsed;
            });
            await session.OpenAsync(new CameraOpenOptions { DesiredFps = 15 }, cancellationToken);
            if (session.State == CameraSessionState.Faulted)
            {
                return;
            }
            await session.StartAsync(cancellationToken);
            CameraPreviewStatusText.Text = "等待相机画面...";
        }
        catch (OperationCanceledException)
        {
            // 切换输入源或离开页面时，正常取消当前预览。
        }
        catch (Exception ex)
        {
            CameraPreviewStatusText.Visibility = Visibility.Visible;
            CameraPreviewStatusText.Text = $"相机预览失败：{ex.Message}";
            if (ReferenceEquals(_cameraPreviewSession, session))
            {
                await DisposeCameraPreviewSessionAsync();
            }
        }
        finally
        {
            _cameraPreviewGate.Release();
            if (ReferenceEquals(_cameraPreviewCts, cts))
            {
                _cameraPreviewCts = null;
            }
            cts.Dispose();
        }
    }

    private async Task StopCameraPreviewAsync()
    {
        _cameraPreviewCts?.Cancel();
        await _cameraPreviewGate.WaitAsync();
        try
        {
            await DisposeCameraPreviewSessionAsync();
        }
        finally
        {
            _cameraPreviewGate.Release();
        }
    }

    private async Task DisposeCameraPreviewSessionAsync()
    {
        var session = _cameraPreviewSession;
        _cameraPreviewSession = null;
        if (session is null)
        {
            return;
        }
        try
        {
            await session.StopAsync(CancellationToken.None);
        }
        catch
        {
            // 即使停止失败也继续释放底层采集资源。
        }
        try
        {
            await session.DisposeAsync();
        }
        catch
        {
            // 预览关闭不应阻塞任务配置页。
        }
    }

    private void ImportTemporalModel_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe)
        {
            return;
        }
        var plugin = SelectedPlugin();
        if (plugin is null)
        {
            ThemedMessageBox.Show("请先选择有效的 YOLO11 插件。", "时序模型导入");
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "选择 ST-GCN / PoseC3D TorchScript 模型",
            Filter = "时序模型 (*.pt;*.ts;*.torchscript)|*.pt;*.ts;*.torchscript|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            TemporalModelPathText.Text = Path.GetFullPath(dialog.FileName);
            RefreshCustomBehaviorClasses();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"时序模型导入失败：{ex.Message}", "时序模型导入", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportModel_Click(object sender, RoutedEventArgs e)
    {
        if (!RolePolicy.CanEditRecipe)
        {
            ThemedMessageBox.Show("当前角色无权导入模型，请切换到工程师或专家模式。", "权限限制");
            return;
        }
        var plugin = SelectedPlugin();
        if (plugin is null)
        {
            ThemedMessageBox.Show("请先选择有效的 YOLO11 插件。", "模型导入");
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "选择 YOLO 模型文件",
            Filter = "YOLO 模型 (*.engine;*.pt;*.onnx)|*.engine;*.pt;*.onnx|TensorRT Engine (*.engine)|*.engine|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var absolutePath = Path.GetFullPath(dialog.FileName);
            if (IsBehaviorModelPath(absolutePath))
            {
                ThemedMessageBox.Show("这是行为时序模型，不能作为顶部 YOLO11 主模型使用。请在行为识别区域的‘行为时序模型’位置导入。", "模型类型不匹配", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!ModelCombo.Items.Contains(absolutePath))
            {
                ModelCombo.Items.Add(absolutePath);
            }
            ModelCombo.SelectedItem = absolutePath;
            ModelStatusText.Text = $"已导入：{absolutePath}";
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"模型导入失败：{ex.Message}", "模型导入", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateDevicePreview()
    {
        if (DevicePreviewText is null)
        {
            return;
        }
        var providerId = SelectedProviderId();
        UpdateInputSourceFields();
        RequestCameraPreviewRefresh();
        if (!string.Equals(providerId, "image-folder", StringComparison.OrdinalIgnoreCase))
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

    private bool IsAtu5Plugin => string.Equals(
        (PluginCombo.SelectedItem as string) ?? PluginCombo.Text,
        "com.vision.atu5", StringComparison.OrdinalIgnoreCase);

    private void EnsureSemanticPluginBinding(bool isSemantic)
    {
        if (!isSemantic || _syncingSemanticPlugin || IsAtu5Plugin || PluginCombo is null)
        {
            return;
        }
        var atu5 = PluginCombo.Items.OfType<string>()
            .FirstOrDefault(id => string.Equals(id, "com.vision.atu5", StringComparison.OrdinalIgnoreCase));
        if (atu5 is null)
        {
            SemanticPluginFixedText.Text = "ATU5/FPN 语义分割插件未找到，请检查 workers/atu5。";
            return;
        }
        try
        {
            _syncingSemanticPlugin = true;
            PluginCombo.SelectedItem = atu5;
        }
        finally
        {
            _syncingSemanticPlugin = false;
        }
    }

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
        var usesModelPlugin = IsYoloPlugin || IsAtu5Plugin;
        ModelGroup.Visibility = usesModelPlugin ? Visibility.Visible : Visibility.Collapsed;
        YoloTaskLabel.Visibility = IsYoloPlugin ? Visibility.Visible : Visibility.Collapsed;
        YoloTaskCombo.Visibility = IsYoloPlugin ? Visibility.Visible : Visibility.Collapsed;
        UpdateTaskParameterVisibility();
        if (!usesModelPlugin)
        {
            return;
        }
        try
        {
            var settings = JsonNode.Parse(string.IsNullOrWhiteSpace(_settingsJson) ? "{}" : _settingsJson)
                as JsonObject;
            if (IsYoloPlugin)
            {
                var task = settings?["task"]?.GetValue<string>() ?? "detect";
                YoloTaskCombo.SelectedItem = YoloTaskCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, task, StringComparison.OrdinalIgnoreCase));
            }
            LoadModelChoices();
            UpdateTaskParameterVisibility();
            var modelPath = settings?["modelPath"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                var absoluteModelPath = ToAbsoluteModelPath(modelPath);
                if (IsYoloPlugin && IsBehaviorModelPath(absoluteModelPath))
                {
                    var defaultModel = PreferredYoloModelPath();
                    ModelCombo.SelectedItem = defaultModel;
                    ModelStatusText.Text = $"已忽略行为时序模型；YOLO11 主模型已切换为：{defaultModel ?? "未找到 YOLO11 模型"}。行为模型请放在下方‘行为时序模型’。";
                }
                else
                {
                    if (!ModelCombo.Items.Contains(absoluteModelPath))
                    {
                        ModelCombo.Items.Add(absoluteModelPath);
                    }
                    ModelCombo.SelectedItem = absoluteModelPath;
                }
            }
        }
        catch
        {
            ModelStatusText.Text = "插件配置不是有效 JSON，无法加载模型设置";
        }
    }

    private void UpdateTaskParameterVisibility()
    {
        if (YoloTaskCombo is null)
        {
            return;
        }

        var task = IsYoloPlugin
            ? (YoloTaskCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "detect"
            : IsAtu5Plugin ? "semantic"
            : "detect";
        var isDetection = string.Equals(task, "detect", StringComparison.OrdinalIgnoreCase);
        var isPose = string.Equals(task, "pose", StringComparison.OrdinalIgnoreCase);
        var isSegmentation = string.Equals(task, "instance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(task, "semantic", StringComparison.OrdinalIgnoreCase);
        var selectedType = SelectedTaskType();
        var isCountingTask = HasTaskTypeSelection() && selectedType.IsCounting();
        var isRegionTask = HasTaskTypeSelection() && selectedType.IsRegion();
        var isBehaviorTask = HasTaskTypeSelection() && selectedType.IsBehavior();

        var selectedModelTask = selectedType.Normalize() == InspectionTaskType.SemanticSegmentation ? "semantic" : "instance";
        if (isRegionTask && IsYoloPlugin && !string.Equals(task, selectedModelTask, StringComparison.OrdinalIgnoreCase))
        {
            var modelTaskItem = YoloTaskCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, selectedModelTask, StringComparison.OrdinalIgnoreCase));
            if (modelTaskItem is not null)
            {
                YoloTaskCombo.SelectedItem = modelTaskItem;
                return;
            }
        }

        // 行为识别必须取得人体关键点；用户选择行为任务后自动切换到 YOLO11 Pose，避免
        // 仍停留在 detect 导致时序模型没有输入。
        if (isBehaviorTask && IsYoloPlugin && !isPose)
        {
            var poseItem = YoloTaskCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, "pose", StringComparison.OrdinalIgnoreCase));
            if (poseItem is not null)
            {
                YoloTaskCombo.SelectedItem = poseItem;
                return;
            }
        }

        SetGroupVisibility(ModeCombo, isDetection && isCountingTask);
        SetGroupVisibility(RoiXText, isCountingTask || isRegionTask);
        SetGroupVisibility(RulesGrid, (isDetection && isCountingTask) || isRegionTask);
        BehaviorGroup.Visibility = isBehaviorTask && (isDetection || isPose)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (isSegmentation)
        {
            BehaviorEnabledCheck.IsChecked = false;
        }
    }

    private static void SetGroupVisibility(FrameworkElement child, bool visible)
    {
        var group = FindParent<GroupBox>(child);
        if (group is not null)
        {
            group.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T match)
            {
                return match;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private void LoadModelChoices()
    {
        if (!IsYoloPlugin && !IsAtu5Plugin)
        {
            return;
        }
        var selected = ModelCombo.SelectedItem as string;
        if (IsBehaviorModelPath(selected))
        {
            selected = null;
        }
        ModelCombo.Items.Clear();
        var plugin = SelectedPlugin();
        if (plugin is not null)
        {
            var modelDirectories = IsAtu5Plugin
                ? new[] { plugin.Directory, Path.Combine(plugin.Directory, "models") }
                : new[] { Path.Combine(plugin.Directory, "models") };
            foreach (var modelsDirectory in modelDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(modelsDirectory)) continue;
                foreach (var file in Directory.EnumerateFiles(modelsDirectory)
                             .Where(file => Path.GetExtension(file).Equals(".pt", StringComparison.OrdinalIgnoreCase)
                                 || Path.GetExtension(file).Equals(".onnx", StringComparison.OrdinalIgnoreCase)
                                 || Path.GetExtension(file).Equals(".engine", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
                {
                    ModelCombo.Items.Add(Path.GetFullPath(file));
                }
            }
        }
        if (selected is not null && ModelCombo.Items.Contains(selected))
        {
            ModelCombo.SelectedItem = selected;
        }
        else if (IsYoloPlugin && PreferredYoloModelPath() is { } preferred)
        {
            ModelCombo.SelectedItem = preferred;
        }
        else if (ModelCombo.Items.Count > 0)
        {
            ModelCombo.SelectedIndex = 0;
        }
        ModelStatusText.Text = ModelCombo.SelectedItem is string path
            ? $"可用模型：{path}"
            : "尚未导入模型文件";
    }

    private string ToAbsoluteModelPath(string modelPath)
    {
        if (Path.IsPathRooted(modelPath)) return Path.GetFullPath(modelPath);
        var plugin = SelectedPlugin();
        return plugin is null
            ? Path.GetFullPath(modelPath)
            : Path.GetFullPath(Path.Combine(plugin.Directory, modelPath));
    }

    private string? PreferredYoloModelPath()
    {
        var task = (YoloTaskCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "detect";
        var expectedName = task switch
        {
            "pose" => "yolo11n-pose.pt",
            "instance" or "semantic" => "yolo11n-seg.pt",
            _ => "yolo11n.pt",
        };
        return ModelCombo.Items.OfType<string>()
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBehaviorModelPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Any(part => string.Equals(part, BehaviorDatasetStore.ModelsDirectoryName, StringComparison.OrdinalIgnoreCase))
                || (string.Equals(Path.GetFileName(fullPath), "best.pt", StringComparison.OrdinalIgnoreCase)
                    && parts.Length >= 3
                    && string.Equals(parts[^2], "models", StringComparison.OrdinalIgnoreCase)
                    && parts.Skip(Math.Max(0, parts.Length - 4))
                        .Any(part => string.Equals(part, "yolo11", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    private string BuildSettingsJson()
    {
        if (!IsYoloPlugin && !IsAtu5Plugin)
        {
            return string.IsNullOrWhiteSpace(_settingsJson) ? "{}" : _settingsJson;
        }
        var settings = JsonNode.Parse(string.IsNullOrWhiteSpace(_settingsJson) ? "{}" : _settingsJson)
            as JsonObject ?? new JsonObject();
        var task = IsAtu5Plugin
            ? "semantic"
            : (YoloTaskCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "detect";
        settings["task"] = task;
        if (ModelCombo.SelectedItem is string modelPath && !string.IsNullOrWhiteSpace(modelPath))
        {
            settings["modelPath"] = IsBehaviorModelPath(modelPath)
                ? PreferredYoloModelPath() ?? modelPath
                : modelPath;
        }
        if (IsAtu5Plugin)
        {
            if (ModelCombo.SelectedItem is not string atu5Model || string.IsNullOrWhiteSpace(atu5Model))
            {
                throw new InvalidOperationException("语义分割任务必须选择 ATU5 .pt 或 .engine 模型。");
            }
            settings["device"] = SelectedExecutionProvider();
            return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        settings["temporalModelType"] = (TemporalModelTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "stgcn";
        settings["temporalSequenceLength"] = 30;
        var temporalModelPath = ResolveTemporalModelPath(TemporalModelPathText.Text.Trim()) ?? "";
        settings["temporalModelPath"] = temporalModelPath;
        settings["temporalBehaviorClasses"] = JsonSerializer.SerializeToNode(_customBehaviorClasses.ToArray());
        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private void LoadTemporalSettings()
    {
        if (!IsYoloPlugin || TemporalModelTypeCombo is null)
        {
            return;
        }
        try
        {
            var settings = JsonNode.Parse(string.IsNullOrWhiteSpace(_settingsJson) ? "{}" : _settingsJson)
                as JsonObject;
            var type = settings?["temporalModelType"]?.GetValue<string>() ?? "stgcn";
            TemporalModelTypeCombo.SelectedItem = TemporalModelTypeCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, type, StringComparison.OrdinalIgnoreCase))
                ?? TemporalModelTypeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
            var savedPath = settings?["temporalModelPath"]?.GetValue<string>() ?? "";
            if (HasTaskTypeSelection()
                && SelectedTaskType().IsBehavior()
                && (string.IsNullOrWhiteSpace(savedPath)
                    || !File.Exists(ResolveTemporalModelPath(savedPath))))
            {
                savedPath = FindLatestCustomBehaviorModel() ?? savedPath;
            }
            TemporalModelPathText.Text = savedPath;
            if (string.IsNullOrWhiteSpace(savedPath))
            {
                LoadSavedBehaviorClasses(settings);
            }
            RefreshCustomBehaviorClasses();
        }
        catch
        {
            TemporalModelTypeCombo.SelectedIndex = 0;
            TemporalModelPathText.Text = "";
            _customBehaviorClasses = [];
            RefreshCustomBehaviorClasses();
        }
    }

    private void TemporalModelPathText_TextChanged(object sender, TextChangedEventArgs e) => RefreshCustomBehaviorClasses();

    private void RefreshCustomBehaviorClasses()
    {
        if (CustomBehaviorClassesPanel is null) return;
        _customBehaviorChecks.Clear();
        CustomBehaviorClassesPanel.Children.Clear();
        _customBehaviorClasses = [];

        var modelPath = TemporalModelPathText.Text.Trim();
        var modelFile = ResolveTemporalModelPath(modelPath);
        if (modelFile is null || !File.Exists(modelFile))
        {
            CustomBehaviorModelStatusText.Text = "未加载自定义行为模型，当前显示的是内置规则。请导入行为训练生成的 best.pt（同目录需要 classes.json）。";
            CustomBehaviorModelStatusText.Foreground = Brushes.DarkOrange;
            CustomBehaviorModelStatusText.Visibility = Visibility.Visible;
            CustomBehaviorClassesPanel.Visibility = Visibility.Collapsed;
            BuiltInBehaviorRulesTitle.Visibility = Visibility.Visible;
            BuiltInBehaviorRulesPanel.Visibility = Visibility.Visible;
            return;
        }

        var classes = ReadBehaviorClasses(modelFile);
        if (classes.Count == 0)
        {
            CustomBehaviorModelStatusText.Text = $"已加载时序模型，但未找到类别文件：{modelFile}";
            CustomBehaviorModelStatusText.Foreground = Brushes.DarkOrange;
            CustomBehaviorModelStatusText.Visibility = Visibility.Visible;
            CustomBehaviorClassesPanel.Visibility = Visibility.Collapsed;
            BuiltInBehaviorRulesTitle.Visibility = Visibility.Visible;
            BuiltInBehaviorRulesPanel.Visibility = Visibility.Visible;
            return;
        }

        _customBehaviorClasses = classes;
        CustomBehaviorModelStatusText.Text = $"自定义行为模型：{modelFile}{Environment.NewLine}类别（来自同目录 classes.json）：";
        CustomBehaviorModelStatusText.Foreground = Brushes.DarkGreen;
        CustomBehaviorModelStatusText.Visibility = Visibility.Visible;
        foreach (var label in classes)
        {
            var check = new CheckBox { Content = label, IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
            _customBehaviorChecks[label] = check;
            CustomBehaviorClassesPanel.Children.Add(check);
        }
        CustomBehaviorClassesPanel.Visibility = Visibility.Visible;
        BuiltInBehaviorRulesTitle.Visibility = Visibility.Collapsed;
        BuiltInBehaviorRulesPanel.Visibility = Visibility.Collapsed;
    }

    private void LoadSavedBehaviorClasses(JsonObject? settings)
    {
        if (settings?["temporalBehaviorClasses"] is not JsonArray array)
        {
            return;
        }

        _customBehaviorClasses = array
            .Select(item => item?.GetValue<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string? FindLatestCustomBehaviorModel()
    {
        var candidates = new List<string>();
        foreach (var root in AppServices.Instance.Datasets.List()
                     .Select(dataset => dataset.RootDirectory)
                     .Where(Directory.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!BehaviorDatasetStore.TryLoadFromRoot(root, out var behaviorDataset) || behaviorDataset is null)
            {
                continue;
            }

            var modelsDirectory = BehaviorDatasetStore.GetModelsDirectory(behaviorDataset.RootDirectory);
            if (!Directory.Exists(modelsDirectory))
            {
                continue;
            }

            foreach (var model in Directory.EnumerateFiles(modelsDirectory, "best.pt", SearchOption.AllDirectories))
            {
                if (ReadBehaviorClasses(model).Count > 0)
                {
                    candidates.Add(model);
                }
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
    }

    private string? ResolveTemporalModelPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Path.IsPathRooted(value)) return Path.GetFullPath(value);
        var plugin = SelectedPlugin();
        return plugin is null ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(plugin.Directory, value));
    }

    private static List<string> ReadBehaviorClasses(string modelPath)
    {
        var candidates = new[]
        {
            Path.ChangeExtension(modelPath, ".json"),
            Path.Combine(Path.GetDirectoryName(modelPath) ?? string.Empty, "classes.json"),
        };
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path));
                var values = node is JsonArray array
                    ? array.Select(item => item?.GetValue<string>())
                    : node?["classes"] is JsonArray objectArray
                        ? objectArray.Select(item => item?.GetValue<string>())
                        : [];
                var labels = values.Where(label => !string.IsNullOrWhiteSpace(label))
                    .Select(label => label!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (labels.Count > 0) return labels;
            }
            catch
            {
                // Ignore an invalid sidecar and try the next supported location.
            }
        }
        return [];
    }

    private void ApplyRolePolicy()
    {
        var canEdit = RolePolicy.CanEditRecipe;
        EditorPanel.IsEnabled = canEdit;
        SaveButton.IsEnabled = canEdit;
        ProviderCombo.IsEnabled = canEdit;
        BrowseDeviceButton.IsEnabled = canEdit;
        ImportModelButton.IsEnabled = canEdit;
        BrowseTemporalModelButton.IsEnabled = canEdit;
        UpdatePostProcessUi();
        UpdateDevicePreview();
    }

    private PostProcessMode SelectedPostProcessMode() =>
        (PostProcessModeCombo.SelectedItem as ComboBoxItem)?.Tag is string tag
        && Enum.TryParse<PostProcessMode>(tag, out var mode)
            ? mode
            : PostProcessMode.VisualRules;

    private void SelectPostProcessMode(PostProcessMode mode)
    {
        var item = PostProcessModeCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, mode.ToString(), StringComparison.OrdinalIgnoreCase));
        PostProcessModeCombo.SelectedItem = item ?? PostProcessModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        UpdatePostProcessUi();
    }

    private void PostProcessMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PostProcessModeCombo is null)
            return;
        _postProcessMode = SelectedPostProcessMode();
        UpdatePostProcessUi();
    }

    private void UpdatePostProcessUi()
    {
        if (PostProcessModeCombo is null || EditPostProcessScriptButton is null || AddRuleButton is null || RulesGrid is null)
            return;
        _postProcessMode = SelectedPostProcessMode();
        var scriptMode = _postProcessMode == PostProcessMode.PythonScript;
        var canEdit = RolePolicy.CanEditRecipe;
        EditPostProcessScriptButton.IsEnabled = canEdit && scriptMode;
        AddRuleButton.IsEnabled = canEdit && !scriptMode;
        RulesGrid.IsEnabled = canEdit && !scriptMode;
        PostProcessHintText.Text = scriptMode
            ? "脚本将接收 JSON 检测结果，并返回保留对象索引及最终 OK/NG。点击右侧按钮编辑脚本。"
            : "使用面积、直径、数量等可视化规则进行筛选。";
    }

    private void EditPostProcessScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PythonScriptDialog(_postProcessScript)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() == true)
        {
            _postProcessScript = dialog.ScriptText;
        }
    }

    private void LoadBehavior(BehaviorRecognitionConfig behavior)
    {
        var zone = behavior.Zones.FirstOrDefault();
        BehaviorEnabledCheck.IsChecked = behavior.Enabled && zone is not null;
        BehaviorPolygonText.Text = zone is null
            ? "0.1,0.1;0.9,0.1;0.9,0.9;0.1,0.9"
            : string.Join(";", zone.Polygon.Select(point =>
                $"{point.X.ToString("0.###", CultureInfo.InvariantCulture)},{point.Y.ToString("0.###", CultureInfo.InvariantCulture)}"));
        IntrusionEnabledCheck.IsChecked = behavior.Intrusion.Enabled;
        LoiteringEnabledCheck.IsChecked = behavior.Loitering.Enabled;
        CrowdingEnabledCheck.IsChecked = behavior.Crowding.Enabled;
        FallEnabledCheck.IsChecked = behavior.Fall.Enabled;
        BehaviorConfirmingSecondsText.Text = behavior.Intrusion.ConfirmingSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        BehaviorLoiterWarningSecondsText.Text = behavior.Loitering.WarningSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        BehaviorLoiterAlarmSecondsText.Text = behavior.Loitering.AlarmSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        BehaviorCrowdWarningCountText.Text = behavior.Crowding.WarningCount.ToString(CultureInfo.InvariantCulture);
        BehaviorCrowdAlarmCountText.Text = behavior.Crowding.AlarmCount.ToString(CultureInfo.InvariantCulture);
        BehaviorConfidenceText.Text = behavior.Intrusion.MinimumConfidence.ToString("0.###", CultureInfo.InvariantCulture);
        LoadTemporalSettings();
    }

    private BehaviorRecognitionConfig BuildBehavior()
    {
        var enabled = BehaviorEnabledCheck.IsChecked == true;
        var polygon = ParseBehaviorPolygon(BehaviorPolygonText.Text);
        if (enabled && polygon.Count < 3)
        {
            throw new ArgumentException("启用行为识别时，监控区域至少需要 3 个点。", nameof(BehaviorPolygonText));
        }

        var confirmingSeconds = ParseBehaviorDouble(BehaviorConfirmingSecondsText.Text, "确认秒数");
        var warningSeconds = ParseBehaviorDouble(BehaviorLoiterWarningSecondsText.Text, "徘徊预警秒数");
        var alarmSeconds = ParseBehaviorDouble(BehaviorLoiterAlarmSecondsText.Text, "徘徊报警秒数");
        var confidence = ParseBehaviorDouble(BehaviorConfidenceText.Text, "最低置信度");
        var warningCount = ParseBehaviorInt(BehaviorCrowdWarningCountText.Text, "聚集预警人数");
        var alarmCount = ParseBehaviorInt(BehaviorCrowdAlarmCountText.Text, "聚集报警人数");
        if (confidence is < 0 or > 1)
        {
            throw new ArgumentException("最低置信度必须在 0~1 之间。", nameof(BehaviorConfidenceText));
        }

        var common = new BehaviorRuleSettings
        {
            MinimumConfidence = confidence,
            ConfirmingSeconds = confirmingSeconds,
            RecoverySeconds = 1,
            CooldownSeconds = 10,
            MaximumMissingSeconds = 2,
        };
        var hasCustomBehaviorModel = _customBehaviorClasses.Count > 0;
        return new BehaviorRecognitionConfig
        {
            Enabled = enabled,
            Zones = enabled
                ? [new BehaviorZone { Id = "behavior-zone-1", Name = "行为识别区域", Polygon = polygon }]
                : [],
            Intrusion = common with { Enabled = !hasCustomBehaviorModel && IntrusionEnabledCheck.IsChecked == true },
            Loitering = common with
            {
                Enabled = !hasCustomBehaviorModel && LoiteringEnabledCheck.IsChecked == true,
                WarningSeconds = warningSeconds,
                AlarmSeconds = alarmSeconds,
            },
            Crowding = common with
            {
                Enabled = !hasCustomBehaviorModel && CrowdingEnabledCheck.IsChecked == true,
                WarningCount = warningCount,
                AlarmCount = alarmCount,
            },
            Fall = common with { Enabled = !hasCustomBehaviorModel && FallEnabledCheck.IsChecked == true },
        };
    }

    private static IReadOnlyList<Contracts.Results.NormalizedPoint> ParseBehaviorPolygon(string text)
    {
        var points = new List<Contracts.Results.NormalizedPoint>();
        foreach (var item in text.Split([';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var values = item.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length != 2
                || !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || x is < 0 or > 1 || y is < 0 or > 1)
            {
                throw new ArgumentException($"区域点“{item}”格式无效，应为 0~1 范围内的 x,y。", nameof(BehaviorPolygonText));
            }
            points.Add(new Contracts.Results.NormalizedPoint { X = x, Y = y });
        }
        return points;
    }

    private static double ParseBehaviorDouble(string text, string name) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : throw new ArgumentException($"{name}必须是非负数字。", name);

    private static int ParseBehaviorInt(string text, string name) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 1
            ? value
            : throw new ArgumentException($"{name}必须是大于 0 的整数。", name);

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

    private string SelectedExecutionProvider() =>
        (ExecutionProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "cpu";

    private void SelectExecutionProvider(string provider)
    {
        var normalized = provider.Equals("cuda", StringComparison.OrdinalIgnoreCase) ? "cuda" : "cpu";
        ExecutionProviderCombo.SelectedItem = ExecutionProviderCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, normalized, StringComparison.OrdinalIgnoreCase));
        if (ExecutionProviderCombo.SelectedItem is null) ExecutionProviderCombo.SelectedIndex = 0;
    }
}
