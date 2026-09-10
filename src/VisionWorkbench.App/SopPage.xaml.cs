using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Application;
using VisionWorkbench.Domain;

namespace VisionWorkbench.App;

/// <summary>SOP 流程模型、独立运行配置与版本管理。</summary>
public partial class SopPage : UserControl
{
    private sealed record InputSourceOption(string DisplayName, string ProviderId, string DeviceId)
    {
        // WPF 收起 ComboBox 时可能使用对象 ToString；确保选中态也使用中文 DisplayName。
        public override string ToString() => DisplayName;
    }

    private sealed record DefinitionRow(SopDefinition Definition)
    {
        public string DisplayName => $"{Definition.Name}  ·  v{Definition.Version}  ·  {GetStatus(Definition.Status)}";
    }

    private sealed class StepEditorRow : INotifyPropertyChanged
    {
        public string Order { get; set; } = "1";
        public string Code { get; set; } = "STEP-01";
        public string Name { get; set; } = "新步骤";
        private string _modelType = "目标检测";
        public string ModelType
        {
            get => _modelType;
            set
            {
                if (string.Equals(_modelType, value, StringComparison.Ordinal)) return;
                _modelType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModelTypeDisplay));
            }
        }
        public string ModelTypeDisplay => GetTaskTypeDisplay(ModelType);
        public string ModelId { get; set; } = "";
        public string ModelVersion { get; set; } = "";
        public string PluginId { get; set; } = "";
        public string ExecutionProvider { get; set; } = "CPU";
        public string SettingsJson { get; set; } = "{}";
        public string ClassId { get; set; } = "class_id";
        public string MinConfidence { get; set; } = "0.60";
        public string StableFrames { get; set; } = "3";
        public string TimeoutSeconds { get; set; } = "30";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private readonly ObservableCollection<StepEditorRow> _steps = [];
    private IReadOnlyList<SopDefinition> _definitions = [];
    private SopDefinition? _selectedDefinition;
    private bool _loading;
    private string? _lastProviderId;
    private bool _suppressProviderChange;
    private IReadOnlySet<string> _availablePluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> PluginIds => _availablePluginIds.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();

    public SopPage()
    {
        InitializeComponent();
        StepsGrid.ItemsSource = _steps;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(_selectedDefinition?.Id);

    private async Task LoadAsync(string? selectId = null)
    {
        _loading = true;
        try
        {
            await LoadExecutionOptionsAsync();
            var catalog = (await AppServices.Instance.SopDefinitions.ListAsync()).ToList();

            // 兼容第一版直接写入任务的 SOP：第一次进入制作页时自动收编到独立目录。
            foreach (var inline in (await AppServices.Instance.Recipes.ListAsync())
                         .Select(item => item.Recipe.Sop?.Definition).OfType<SopDefinition>())
            {
                if (catalog.Any(item => string.Equals(item.Id, inline.Id, StringComparison.Ordinal)))
                {
                    continue;
                }
                catalog.Add(inline);
                await AppServices.Instance.SopDefinitions.SaveAsync(inline);
            }

            _definitions = catalog
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var definitionRows = _definitions.Select(item => new DefinitionRow(item)).ToArray();
            DefinitionList.ItemsSource = definitionRows;

            var selected = definitionRows.FirstOrDefault(item =>
                string.Equals(item.Definition.Id, selectId ?? _selectedDefinition?.Id, StringComparison.Ordinal));
            if (selected is not null)
            {
                DefinitionList.SelectedItem = selected;
                // LoadAsync suppresses SelectionChanged while rebuilding the list;
                // explicitly refresh the editor so binding status and step data are current.
                _selectedDefinition = selected.Definition;
                LoadDefinition(selected.Definition);
            }
            else if (definitionRows.Length > 0)
            {
                DefinitionList.SelectedIndex = 0;
            }
            else
            {
                ClearEditor();
                StatusText.Text = "暂无SOP定义，请点击“新建SOP”开始制作。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载失败：{ex.Message}";
            ThemedMessageBox.Show($"加载 SOP 定义失败：{ex.Message}", "SOP流程", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _loading = false;
        }
    }

    private void DefinitionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DefinitionList.SelectedItem is not DefinitionRow row)
        {
            return;
        }

        _selectedDefinition = row.Definition;
        LoadDefinition(row.Definition);
    }

    private async Task LoadExecutionOptionsAsync()
    {
        _availablePluginIds = AppServices.Instance.AlgorithmManager.ScanPlugins()
            .Where(plugin => plugin.Status == Contracts.Plugins.PluginStatus.Valid && plugin.Manifest is not null)
            .Select(plugin => plugin.Manifest!.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Root.DataContext = this;

        var sources = new List<InputSourceOption>
        {
            new("离线图片目录（image-folder）", "image-folder", ""),
            new("视频文件（video-file）", "video-file", ""),
        };
        try
        {
            var cameras = await AppServices.Instance.Cameras.DiscoverAllAsync(CancellationToken.None);
            sources.AddRange(cameras.Select(camera => new InputSourceOption(
                $"{camera.DisplayName}（{camera.ProviderId}/{camera.DeviceId}）",
                camera.ProviderId,
                camera.DeviceId)));
        }
        catch
        {
            // 设备发现失败时仍保留离线输入源。
        }
        SopProviderCombo.ItemsSource = sources
            .DistinctBy(source => $"{source.ProviderId}/{source.DeviceId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (SopProviderCombo.SelectedItem is null)
        {
            SopProviderCombo.SelectedIndex = 0;
        }
    }

    private void SelectExecutionProfile(SopExecutionProfile? profile)
    {
        var execution = profile ?? new SopExecutionProfile
        {
            CameraProviderId = "image-folder",
            CameraDeviceId = "",
        };
        SelectRunMode(execution.RunMode);
        SopDeviceText.Text = execution.CameraDeviceId;

        var options = SopProviderCombo.Items.OfType<InputSourceOption>().ToList();
        var source = options.FirstOrDefault(item =>
            string.Equals(item.ProviderId, execution.CameraProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.DeviceId, execution.CameraDeviceId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            source = new InputSourceOption(
                $"未发现设备（{execution.CameraProviderId}/{execution.CameraDeviceId}）",
                execution.CameraProviderId,
                execution.CameraDeviceId);
            options.Add(source);
            SopProviderCombo.ItemsSource = options;
        }
        _suppressProviderChange = true;
        try { SopProviderCombo.SelectedItem = source; }
        finally { _suppressProviderChange = false; }
        _lastProviderId = execution.CameraProviderId;
    }

    private void SopProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProviderChange || SopProviderCombo.SelectedItem is not InputSourceOption source)
        {
            return;
        }
        if (!IsPathInputSource(source.ProviderId))
        {
            SopDeviceText.Text = source.DeviceId;
        }
        else if (_lastProviderId is not null && !IsPathInputSource(_lastProviderId))
        {
            SopDeviceText.Text = "";
        }
        _lastProviderId = source.ProviderId;
    }

    private static bool IsPathInputSource(string providerId) =>
        string.Equals(providerId, "image-folder", StringComparison.OrdinalIgnoreCase)
        || string.Equals(providerId, "video-file", StringComparison.OrdinalIgnoreCase);

    private void SelectRunMode(SopRunMode mode)
    {
        SopRunModeCombo.SelectedItem = SopRunModeCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? SopRunModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _selectedDefinition = null;
        DefinitionList.SelectedItem = null;
        ClearEditor();
        EditorPanel.IsEnabled = true;
        DefinitionNameText.Focus();
        StatusText.Text = "正在制作新SOP草稿，填写步骤后点击“保存”。";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        try
        {
            if (_selectedDefinition?.Status == SopDefinitionStatus.Published)
            {
                ThemedMessageBox.Show("已发布版本不能原位修改。请点击“新建SOP”制作新版本，避免影响已经投产的任务。",
                    "SOP版本保护", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var definition = BuildDefinition();
            await AppServices.Instance.SopDefinitions.SaveAsync(definition);
            await SyncStandaloneRuntimeTaskAsync(definition);
            _selectedDefinition = definition;
            StatusText.Text = $"已保存：{definition.Name} v{definition.Version}（{GetStatus(definition.Status)}）";
            await LoadAsync(definition.Id);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(ex.Message, "保存SOP失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDefinition is not { } definition)
        {
            ThemedMessageBox.Show("请先选择要删除的 SOP。", "SOP流程", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tasks = await AppServices.Instance.Recipes.ListAsync();
        var runtimeTasks = tasks
            .Where(item => string.Equals(item.Recipe.Sop?.DefinitionId, definition.Id, StringComparison.Ordinal)
                && item.Recipe.Description.StartsWith("SOP独立任务：", StringComparison.Ordinal))
            .ToArray();

        if (ThemedMessageBox.Show($"确认删除 SOP“{definition.Name}”吗？", "删除SOP",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var runtimeTask in runtimeTasks)
            {
                await AppServices.Instance.Recipes.DeleteAsync(runtimeTask.Entity.Id);
            }
            await AppServices.Instance.SopDefinitions.DeleteAsync(definition.Id);
            _selectedDefinition = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"删除 SOP 失败：{ex.Message}", "SOP流程", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SyncStandaloneRuntimeTaskAsync(SopDefinition definition)
    {
        if (definition.Execution is not { } execution)
        {
            throw new InvalidOperationException("SOP 缺少独立执行配置。");
        }
        var firstStepExecution = definition.Steps
            .OrderBy(step => step.Order)
            .Select(step => step.Execution)
            .FirstOrDefault(item => item is not null)
            ?? new SopStepExecution
            {
                PluginId = execution.PluginId,
                TaskType = execution.TaskType,
                ExecutionProvider = execution.ExecutionProvider,
                SettingsJson = execution.SettingsJson,
                Roi = execution.Roi,
                RoiPolicy = execution.RoiPolicy,
                Rules = execution.Rules,
            };
        if (string.IsNullOrWhiteSpace(firstStepExecution.PluginId))
        {
            throw new InvalidOperationException("SOP 至少需要为一个工序配置算法插件。");
        }
        var existing = (await AppServices.Instance.Recipes.ListAsync())
            .FirstOrDefault(item => string.Equals(item.Recipe.Sop?.DefinitionId, definition.Id, StringComparison.Ordinal));
        var binding = new SopBinding
        {
            DefinitionId = definition.Id,
            Version = definition.Version,
            RunMode = execution.RunMode,
            Definition = definition,
        };
        var recipe = new Recipe
        {
            StationCode = execution.StationCode,
            Name = $"SOP · {definition.Name} · v{definition.Version}",
            Description = $"SOP独立任务：{definition.Code}",
            CameraProviderId = execution.CameraProviderId,
            CameraDeviceId = execution.CameraDeviceId,
            PluginId = firstStepExecution.PluginId,
            ExecutionProvider = firstStepExecution.ExecutionProvider,
            TaskType = firstStepExecution.TaskType,
            SettingsJson = firstStepExecution.SettingsJson,
            Roi = firstStepExecution.Roi,
            RoiPolicy = firstStepExecution.RoiPolicy,
            CountingMode = execution.CountingMode,
            CountingLine = execution.CountingLine,
            Behavior = execution.Behavior,
            PostProcess = execution.PostProcess,
            Rules = firstStepExecution.Rules,
            Sop = binding,
        };
        long? existingId = existing.Entity is null ? null : existing.Entity.Id;
        await AppServices.Instance.Recipes.SaveAsync(recipe, existingId);
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        var number = _steps.Count + 1;
        var row = new StepEditorRow
        {
            Order = number.ToString(CultureInfo.InvariantCulture),
            Code = $"STEP-{number:00}",
            Name = $"步骤{number}",
        };
        _steps.Add(row);
        StepsGrid.SelectedItem = row;
        StepsGrid.ScrollIntoView(row);
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (StepsGrid.SelectedItem is StepEditorRow row)
        {
            _steps.Remove(row);
            return;
        }
        if (_steps.Count > 0)
        {
            _steps.RemoveAt(_steps.Count - 1);
        }
    }

    private void LoadDefinition(SopDefinition definition)
    {
        EditorPanel.IsEnabled = true;
        DefinitionIdText.Text = definition.Id;
        DefinitionNameText.Text = definition.Name;
        DefinitionCodeText.Text = definition.Code;
        ProductCodeText.Text = definition.ProductCode;
        VersionText.Text = definition.Version.ToString(CultureInfo.InvariantCulture);
        SelectStatus(definition.Status);
        SelectExecutionProfile(definition.Execution);
        _steps.Clear();
        foreach (var step in definition.Steps.OrderBy(item => item.Order))
        {
            var condition = step.Conditions.FirstOrDefault();
            var execution = step.Execution ?? LegacyStepExecution(definition.Execution, step);
            _steps.Add(new StepEditorRow
            {
                Order = step.Order.ToString(CultureInfo.InvariantCulture),
                Code = step.Code,
                Name = step.Name,
                ModelType = GetTaskTypeDisplay(execution.TaskType.ToString()),
                ModelId = execution.ModelId,
                ModelVersion = execution.ModelVersion,
                PluginId = execution.PluginId,
                ExecutionProvider = GetExecutionProviderDisplay(execution.ExecutionProvider),
                SettingsJson = string.IsNullOrWhiteSpace(execution.SettingsJson) ? "{}" : execution.SettingsJson,
                ClassId = condition?.ClassId ?? condition?.EventType ?? "class_id",
                MinConfidence = (condition?.MinConfidence ?? 0.6).ToString("0.##", CultureInfo.InvariantCulture),
                StableFrames = step.MinimumStableFrames.ToString(CultureInfo.InvariantCulture),
                TimeoutSeconds = step.TimeoutSeconds.ToString("0.##", CultureInfo.InvariantCulture),
            });
        }
        StatusText.Text = definition.Status == SopDefinitionStatus.Published
            ? "已发布版本只读保护：如需修改，请点击“新建SOP”制作新版本。"
            : $"当前编辑：{definition.Name} v{definition.Version}";
    }

    private void ClearEditor()
    {
        EditorPanel.IsEnabled = false;
        DefinitionIdText.Text = "保存时生成";
        DefinitionNameText.Text = "新建SOP流程";
        DefinitionCodeText.Text = $"SOP-{DateTime.Now:yyyyMMddHHmmss}";
        ProductCodeText.Text = "";
        VersionText.Text = "1";
        SelectStatus(SopDefinitionStatus.Draft);
        SelectExecutionProfile(null);
        _steps.Clear();
    }

    private static SopStepExecution LegacyStepExecution(SopExecutionProfile? profile, SopStep step)
    {
        if (profile is null)
        {
            return new SopStepExecution();
        }
        return new SopStepExecution
        {
            ModelId = $"legacy-{step.Code}",
            PluginId = profile.PluginId,
            TaskType = profile.TaskType,
            ExecutionProvider = profile.ExecutionProvider,
            SettingsJson = profile.SettingsJson,
            Roi = profile.Roi,
            RoiPolicy = profile.RoiPolicy,
            Rules = profile.Rules,
        };
    }

    private SopDefinition BuildDefinition()
    {
        var name = DefinitionNameText.Text.Trim();
        var code = DefinitionCodeText.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("流程名称和流程编号不能为空。\n请先填写基本信息。");
        }
        if (!int.TryParse(VersionText.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new InvalidOperationException("版本必须是大于等于 1 的整数。");
        }
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("SOP 至少需要配置一个工序步骤。");
        }

        var parsedSteps = new List<SopStep>(_steps.Count);
        foreach (var row in _steps)
        {
            if (!int.TryParse(row.Order.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) || order < 1)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的顺序必须是大于等于 1 的整数。");
            }
            if (string.IsNullOrWhiteSpace(row.Code) || string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.ClassId))
            {
                throw new InvalidOperationException("每个步骤都必须填写步骤编号、步骤名称和类别/匹配值。");
            }
            if (!TryParseTaskType(row.ModelType, out var taskType))
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的模型类型无效：{row.ModelType}。");
            }
            if (string.IsNullOrWhiteSpace(row.PluginId))
            {
                throw new InvalidOperationException($"步骤“{row.Name}”必须填写算法插件 ID。");
            }
            if (_availablePluginIds.Count > 0 && !_availablePluginIds.Contains(row.PluginId.Trim()))
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的算法插件不存在或未通过校验：{row.PluginId}。");
            }
            if (!TryParseExecutionProvider(row.ExecutionProvider, out var executionProvider))
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的推理设备只能填写 cpu 或 cuda。");
            }
            var settingsJson = string.IsNullOrWhiteSpace(row.SettingsJson) ? "{}" : row.SettingsJson.Trim();
            try
            {
                if (JsonDocument.Parse(settingsJson).RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"步骤“{row.Name}”的模型参数必须是 JSON 对象。");
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的模型参数不是合法 JSON：{ex.Message}");
            }
            if (!double.TryParse(row.MinConfidence.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)
                || confidence is < 0 or > 1)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的置信度必须在 0 到 1 之间。");
            }
            if (!int.TryParse(row.StableFrames.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stableFrames) || stableFrames < 1)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的稳定帧数必须大于等于 1。");
            }
            if (!double.TryParse(row.TimeoutSeconds.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var timeout) || timeout < 0)
            {
                throw new InvalidOperationException($"步骤“{row.Name}”的超时秒数必须是非负数。");
            }

            var stepId = $"{code}-step-{order:00}";
            parsedSteps.Add(new SopStep
            {
                Id = stepId,
                Order = order,
                Code = row.Code.Trim(),
                Name = row.Name.Trim(),
                Required = true,
                EnforceOrder = true,
                TimeoutSeconds = timeout,
                MinimumStableFrames = stableFrames,
                Execution = new SopStepExecution
                {
                    ModelId = string.IsNullOrWhiteSpace(row.ModelId) ? $"{code}-model-{order:00}" : row.ModelId.Trim(),
                    ModelVersion = row.ModelVersion.Trim(),
                    PluginId = row.PluginId.Trim(),
                    TaskType = taskType,
                    ExecutionProvider = executionProvider,
                    SettingsJson = settingsJson,
                    Rules = BuildStepRules(taskType, row.ClassId.Trim()),
                },
                Conditions =
                [
                    BuildCondition(stepId, taskType, row.ClassId.Trim(), confidence),
                ],
            });
        }

        if (parsedSteps.Select(step => step.Order).Distinct().Count() != parsedSteps.Count)
        {
            throw new InvalidOperationException("步骤顺序不能重复，请按 1、2、3……配置。");
        }

        var status = Enum.TryParse<SopDefinitionStatus>((StatusCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var parsedStatus)
            ? parsedStatus
            : SopDefinitionStatus.Draft;
        return new SopDefinition
        {
            Id = _selectedDefinition?.Id ?? $"sop:{Guid.NewGuid():N}",
            Code = code,
            Name = name,
            ProductCode = ProductCodeText.Text.Trim(),
            Version = version,
            Status = status,
            Execution = BuildExecutionProfile(parsedSteps),
            Steps = parsedSteps.OrderBy(step => step.Order).ToArray(),
        };
    }

    private SopExecutionProfile BuildExecutionProfile(IReadOnlyList<SopStep> steps)
    {
        var source = SopProviderCombo.SelectedItem as InputSourceOption;
        var providerId = source?.ProviderId ?? "image-folder";
        var deviceId = IsPathInputSource(providerId) ? SopDeviceText.Text.Trim() : source?.DeviceId ?? SopDeviceText.Text.Trim();
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(deviceId))
        {
            throw new InvalidOperationException("SOP 执行配置必须选择输入源并填写设备/路径。");
        }

        var runMode = Enum.TryParse<SopRunMode>(
            (SopRunModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var parsedRunMode)
            ? parsedRunMode
            : SopRunMode.StrictOrder;
        var first = steps.OrderBy(step => step.Order).Select(step => step.Execution).FirstOrDefault(item => item is not null)
            ?? throw new InvalidOperationException("SOP 至少需要为一个工序配置模型。");
        return new SopExecutionProfile
        {
            StationCode = $"SOP-{DefinitionCodeText.Text.Trim()}",
            CameraProviderId = providerId,
            CameraDeviceId = deviceId,
            PluginId = first.PluginId,
            ExecutionProvider = first.ExecutionProvider,
            TaskType = first.TaskType,
            SettingsJson = first.SettingsJson,
            Roi = first.Roi,
            CountingMode = CountingMode.Snapshot,
            RunMode = runMode,
            Rules = first.Rules,
        };
    }

    private static SopCondition BuildCondition(string stepId, InspectionTaskType taskType, string value, double confidence)
    {
        var normalized = taskType.Normalize();
        return normalized switch
        {
            InspectionTaskType.AiText => new SopCondition
            {
                Id = $"{stepId}-condition", Kind = SopConditionKind.TextEquals,
                EventType = "text.recognized", TextValue = value,
                MinConfidence = confidence, Required = true,
            },
            InspectionTaskType.Barcode or InspectionTaskType.QrCode => new SopCondition
            {
                Id = $"{stepId}-condition", Kind = SopConditionKind.CodeEquals,
                EventType = "code.recognized", CodeValue = value,
                MinConfidence = confidence, Required = true,
            },
            InspectionTaskType.BehaviorRecognition => new SopCondition
            {
                Id = $"{stepId}-condition", Kind = SopConditionKind.BehaviorCompleted,
                EventType = "behavior.completed", ClassId = value,
                MinConfidence = confidence, Required = true,
            },
            _ => new SopCondition
            {
                Id = $"{stepId}-condition", Kind = SopConditionKind.ObjectPresent,
                EventType = "object.present", ClassId = value,
                MinConfidence = confidence, Required = true,
            },
        };
    }

    private static IReadOnlyList<InspectionRule> BuildStepRules(InspectionTaskType taskType, string value)
        => taskType.Normalize() is InspectionTaskType.Detection or InspectionTaskType.BehaviorRecognition
            ? [new InspectionRule
            {
                RuleId = "sop-step-check", Kind = RuleKind.CountAtLeast,
                ClassId = value, ExpectedCount = 1, MinConfidence = 0.1, Enabled = true,
            }]
            : [];

    private void CommitGridEdits()
    {
        StepsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void SelectStatus(SopDefinitionStatus status)
    {
        StatusCombo.SelectedItem = StatusCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), status.ToString(), StringComparison.Ordinal));
    }

    private static string GetTaskTypeDisplay(string value)
        => TryParseTaskType(value, out var type)
            ? type.Normalize() switch
            {
                InspectionTaskType.Detection => "目标检测",
                InspectionTaskType.SemanticSegmentation => "语义分割",
                InspectionTaskType.InstanceSegmentation => "实例分割",
                InspectionTaskType.BehaviorRecognition => "行为识别",
                InspectionTaskType.AiText => "AI字符识别",
                InspectionTaskType.Barcode => "AI条码识别",
                InspectionTaskType.QrCode => "AI二维码识别",
                _ => value,
            }
            : value;

    private static bool TryParseTaskType(string? value, out InspectionTaskType type)
    {
        if (Enum.TryParse<InspectionTaskType>(value, true, out type) && Enum.IsDefined(type)) return true;
        type = value?.Trim() switch
        {
            "目标检测" => InspectionTaskType.Detection,
            "语义分割" => InspectionTaskType.SemanticSegmentation,
            "实例分割" => InspectionTaskType.InstanceSegmentation,
            "行为识别" => InspectionTaskType.BehaviorRecognition,
            "AI字符识别" => InspectionTaskType.AiText,
            "AI条码识别" => InspectionTaskType.Barcode,
            "AI二维码识别" => InspectionTaskType.QrCode,
            _ => (InspectionTaskType)(-1),
        };
        return Enum.IsDefined(type);
    }

    private static string GetExecutionProviderDisplay(string value)
        => value.Equals("cuda", StringComparison.OrdinalIgnoreCase) || value.Equals("CUDA", StringComparison.OrdinalIgnoreCase)
            ? "CUDA"
            : "CPU";

    private static bool TryParseExecutionProvider(string? value, out string provider)
    {
        provider = value?.Trim().ToLowerInvariant() switch
        {
            "cpu" or "CPU" => "cpu",
            "cuda" or "CUDA" or "cuda（gpu）" => "cuda",
            _ => "",
        };
        return provider is "cpu" or "cuda";
    }

    private static string GetStatus(SopDefinitionStatus status) => status switch
    {
        SopDefinitionStatus.Published => "已发布",
        SopDefinitionStatus.Retired => "已停用",
        _ => "草稿",
    };
}
