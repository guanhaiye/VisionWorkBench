using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VisionWorkbench.App;

/// <summary>数据与模型总览：左侧按任务类型选择，右侧按流程节点切换平台。</summary>
public partial class DataModelPage : UserControl
{
    private readonly Dictionary<string, UserControl> _pageCache = new(StringComparer.OrdinalIgnoreCase);
    private string _category = "detection";

    public DataModelPage()
    {
        InitializeComponent();
        SelectCategory("detection");
    }

    public void SelectCategory(string category)
    {
        if (category is not ("detection" or "semantic-segmentation" or "instance-segmentation" or "segmentation" or "behavior" or "ai-text" or "barcode" or "qrcode"))
        {
            category = "detection";
        }

        _category = category;
        CategoryTitleText.Text = category switch
        {
            "semantic-segmentation" or "segmentation" => "语义分割",
            "instance-segmentation" => "实例分割",
            "behavior" => "行为识别",
            "ai-text" => "AI字符识别",
            "barcode" => "AI条码识别",
            "qrcode" => "AI二维码识别",
            _ => "目标检测",
        };
        DetectionWorkflowPanel.Visibility = category == "detection" ? Visibility.Visible : Visibility.Collapsed;
        SemanticSegmentationWorkflowPanel.Visibility = category is "semantic-segmentation" or "segmentation" ? Visibility.Visible : Visibility.Collapsed;
        InstanceSegmentationWorkflowPanel.Visibility = category == "instance-segmentation" ? Visibility.Visible : Visibility.Collapsed;
        BehaviorWorkflowPanel.Visibility = category == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        AiTextWorkflowPanel.Visibility = category == "ai-text" ? Visibility.Visible : Visibility.Collapsed;
        BarcodeWorkflowPanel.Visibility = category == "barcode" ? Visibility.Visible : Visibility.Collapsed;
        QrCodeWorkflowPanel.Visibility = category == "qrcode" ? Visibility.Visible : Visibility.Collapsed;

        ShowPage(category == "segmentation" ? "segmentation-annotation"
            : category == "semantic-segmentation" ? "semantic-segmentation-annotation"
            : category == "instance-segmentation" ? "instance-segmentation-annotation"
            : category == "behavior" ? "behavior-collection"
            : category == "ai-text" ? "ai-text-annotation"
            : category == "barcode" ? "barcode-annotation"
            : category == "qrcode" ? "qrcode-annotation"
            : "detection-annotation");
    }

    private void WorkflowNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string pageKey })
        {
            ShowPage(pageKey);
        }
    }

    private void AdaptiveWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private void RestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.WindowState = WindowState.Normal;
        }
    }

    private void ShowPage(string pageKey)
    {
        if (!_pageCache.TryGetValue(pageKey, out var page))
        {
            page = CreatePage(pageKey);
            _pageCache[pageKey] = page;
        }
        PlatformHost.Content = page;
        UpdateNodeStyles(pageKey);
    }

    private UserControl CreatePage(string pageKey) => pageKey switch
    {
        "detection-annotation" => new DatasetAnnotationPage(AnnotationPlatform.Detection),
        "segmentation-annotation" => new DatasetAnnotationPage(AnnotationPlatform.Segmentation),
        "semantic-segmentation-annotation" => new DatasetAnnotationPage(AnnotationPlatform.SemanticSegmentation),
        "instance-segmentation-annotation" => new DatasetAnnotationPage(AnnotationPlatform.InstanceSegmentation),
        "behavior-collection" => new BehaviorCollectionPage(),
        "behavior-annotation" => new BehaviorAnnotationPage(),
        "behavior-training" => new BehaviorTrainingPage(),
        "live" => new LivePage(),
        "ai-text-annotation" => new AiCharacterRecognitionPage(AiCharacterPlatform.Annotation),
        "ai-text-training" => new AiCharacterRecognitionPage(AiCharacterPlatform.Training),
        "ai-text-test" => new AiCharacterRecognitionPage(AiCharacterPlatform.Test),
        "barcode-annotation" => new AiCharacterRecognitionPage(AiCharacterPlatform.Annotation, AiRecognitionKind.Barcode),
        "barcode-training" => new AiCharacterRecognitionPage(AiCharacterPlatform.Training, AiRecognitionKind.Barcode),
        "barcode-test" => new AiCharacterRecognitionPage(AiCharacterPlatform.Test, AiRecognitionKind.Barcode),
        "qrcode-annotation" => new AiCharacterRecognitionPage(AiCharacterPlatform.Annotation, AiRecognitionKind.QrCode),
        "qrcode-training" => new AiCharacterRecognitionPage(AiCharacterPlatform.Training, AiRecognitionKind.QrCode),
        "qrcode-test" => new AiCharacterRecognitionPage(AiCharacterPlatform.Test, AiRecognitionKind.QrCode),
        "training" => new TrainingPage(),
        "semantic-segmentation-training" => new TrainingPage("semantic_segmentation"),
        "instance-segmentation-training" => new TrainingPage("instance_segmentation"),
        "model-test" when _category is "segmentation" or "semantic-segmentation" => new ModelTestPage(true),
        "semantic-segmentation-test" => new ModelTestPage(true),
        "instance-segmentation-test" => new ModelTestPage(false, "YOLO11-seg 实例分割"),
        "model-test" => new ModelTestPage(),
        _ => new WelcomePage(),
    };

    private void UpdateNodeStyles(string pageKey)
    {
        foreach (var node in new[]
        {
            DetectionAnnotationNode, DetectionTrainingNode, DetectionTestNode,
            SemanticSegmentationAnnotationNode, SemanticSegmentationTrainingNode, SemanticSegmentationTestNode,
            InstanceSegmentationAnnotationNode, InstanceSegmentationTrainingNode, InstanceSegmentationTestNode,
            BehaviorCollectionNode, BehaviorAnnotationNode, BehaviorTrainingNode, BehaviorTestNode,
            AiTextAnnotationNode, AiTextTrainingNode, AiTextTestNode,
            BarcodeAnnotationNode, BarcodeTrainingNode, BarcodeTestNode,
            QrCodeAnnotationNode, QrCodeTrainingNode, QrCodeTestNode,
        })
        {
            node.Background = ResourceBrush("SurfaceAltBrush", Brushes.White);
            node.Foreground = ResourceBrush("TextBrush", Brushes.Black);
            node.BorderBrush = ResourceBrush("BorderBrush", Brushes.LightGray);
        }

        var active = pageKey switch
        {
            "detection-annotation" => DetectionAnnotationNode,
            "training" when _category == "detection" => DetectionTrainingNode,
            "model-test" when _category == "detection" => DetectionTestNode,
            "segmentation-annotation" or "semantic-segmentation-annotation" => SemanticSegmentationAnnotationNode,
            "semantic-segmentation-training" => SemanticSegmentationTrainingNode,
            "semantic-segmentation-test" => SemanticSegmentationTestNode,
            "training" when _category is "segmentation" or "semantic-segmentation" => SemanticSegmentationTrainingNode,
            "model-test" when _category is "segmentation" or "semantic-segmentation" => SemanticSegmentationTestNode,
            "instance-segmentation-annotation" => InstanceSegmentationAnnotationNode,
            "instance-segmentation-training" => InstanceSegmentationTrainingNode,
            "instance-segmentation-test" => InstanceSegmentationTestNode,
            "behavior-collection" => BehaviorCollectionNode,
            "behavior-annotation" => BehaviorAnnotationNode,
            "behavior-training" => BehaviorTrainingNode,
            "live" => BehaviorTestNode,
            "ai-text-annotation" => AiTextAnnotationNode,
            "ai-text-training" => AiTextTrainingNode,
            "ai-text-test" => AiTextTestNode,
            "barcode-annotation" => BarcodeAnnotationNode,
            "barcode-training" => BarcodeTrainingNode,
            "barcode-test" => BarcodeTestNode,
            "qrcode-annotation" => QrCodeAnnotationNode,
            "qrcode-training" => QrCodeTrainingNode,
            "qrcode-test" => QrCodeTestNode,
            _ => null,
        };
        if (active is not null)
        {
            active.Background = ResourceBrush("AccentBrush", Brushes.DodgerBlue);
            active.Foreground = Brushes.White;
            active.BorderBrush = ResourceBrush("AccentBrush", Brushes.DodgerBlue);
        }
    }

    private Brush ResourceBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;
}
