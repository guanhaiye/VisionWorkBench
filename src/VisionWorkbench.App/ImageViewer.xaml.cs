using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VisionWorkbench.App;

/// <summary>
/// 统一图片查看控件：固定视口、滚轮缩放、以鼠标为中心、滚动条和右键自适应。
/// 业务页面只负责提供图片及 Canvas 等叠加内容。
/// </summary>
public class ImageViewer : ContentControl
{
    private ScrollViewer? _viewer;
    private Grid? _scrollContent;
    private ContentControl? _contentHost;
    private ScaleTransform? _contentScale;
    private double _baseWidth;
    private double _baseHeight;
    private bool _middleDragging;
    private Point _middleDragStart;
    private double _middleDragHorizontalOffset;
    private double _middleDragVerticalOffset;

    public ImageViewer()
    {
        ClipToBounds = true;
        // ContentControl 默认按内容期望尺寸对齐，会让预览内容停留在左上角。
        // 预览画布必须填满可用视口，内部 Image 再通过 Uniform 保持原图比例。
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Template = CreateTemplate();
    }

    public double Zoom => _contentScale?.ScaleX ?? 1;

    /// <summary>允许业务页面暂时接管滚轮，例如标注页调整橡皮擦直径。</summary>
    public bool WheelZoomEnabled { get; set; } = true;

    public event EventHandler<double>? ZoomChanged;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _viewer = Template.FindName("Viewer", this) as ScrollViewer;
        _scrollContent = Template.FindName("ScrollContent", this) as Grid;
        _contentHost = Template.FindName("ContentHost", this) as ContentControl;
        if (_contentHost is not null)
        {
            // Freezables stored in a shared ControlTemplate can be frozen by WPF.
            // Give every viewer its own mutable transform before zoom code updates it.
            _contentScale = new ScaleTransform(1, 1);
            _contentHost.RenderTransform = _contentScale;
        }

        if (_viewer is null)
        {
            return;
        }

        _viewer.PreviewMouseWheel += Viewer_PreviewMouseWheel;
        _viewer.PreviewMouseDown += Viewer_PreviewMouseDown;
        _viewer.PreviewMouseMove += Viewer_PreviewMouseMove;
        _viewer.PreviewMouseUp += Viewer_PreviewMouseUp;
        _viewer.Loaded += Viewer_Loaded;
        _viewer.SizeChanged += Viewer_SizeChanged;

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = "放大",
            Command = new ActionCommand(() => ZoomAt(Zoom * 1.25, ViewerCenter()))
        });
        menu.Items.Add(new MenuItem
        {
            Header = "缩小",
            Command = new ActionCommand(() => ZoomAt(Zoom / 1.25, ViewerCenter()))
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "自适应窗口",
            Command = new ActionCommand(FitToWindow)
        });
        _viewer.ContextMenu = menu;

        UpdateContentSize();
    }

    public void FitToWindow()
    {
        if (_viewer is null || _contentScale is null)
        {
            return;
        }

        _contentScale.ScaleX = 1;
        _contentScale.ScaleY = 1;
        // Content/source changes can happen after the viewer's first measure pass.
        // Refresh the viewport before capturing the fit size, otherwise the old
        // (often image-sized) extent can leave the preview stuck in the top-left.
        _viewer.UpdateLayout();
        UpdateContentSize(resetBase: true);
        _viewer.UpdateLayout();
        _viewer.ScrollToHorizontalOffset(0);
        _viewer.ScrollToVerticalOffset(0);
        ZoomChanged?.Invoke(this, 1);
    }

    private static ControlTemplate CreateTemplate()
    {
        var template = new ControlTemplate(typeof(ImageViewer));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));

        var root = new FrameworkElementFactory(typeof(Grid));
        var viewer = new FrameworkElementFactory(typeof(ScrollViewer));
        viewer.Name = "Viewer";
        viewer.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        viewer.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        viewer.SetValue(ScrollViewer.PanningModeProperty, PanningMode.Both);

        var scrollContent = new FrameworkElementFactory(typeof(Grid));
        scrollContent.Name = "ScrollContent";
        scrollContent.SetValue(FrameworkElement.WidthProperty, 1d);
        scrollContent.SetValue(FrameworkElement.HeightProperty, 1d);
        scrollContent.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        scrollContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);

        var contentHost = new FrameworkElementFactory(typeof(ContentControl));
        contentHost.Name = "ContentHost";
        contentHost.SetValue(FrameworkElement.WidthProperty, 1d);
        contentHost.SetValue(FrameworkElement.HeightProperty, 1d);
        contentHost.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        contentHost.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        // 内容宿主必须填满视口，避免检测开始后画面退回左上角的原始尺寸；
        // 预览画布必须填满视口，内部 Image 再通过 Stretch=Uniform 保持原图比例。
        contentHost.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
        contentHost.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch);
        contentHost.SetValue(ContentControl.ContentProperty, new TemplateBindingExtension(ContentProperty));
        contentHost.SetValue(ContentControl.ContentTemplateProperty, new TemplateBindingExtension(ContentTemplateProperty));
        contentHost.SetValue(UIElement.RenderTransformOriginProperty, new Point(0, 0));

        contentHost.SetValue(UIElement.RenderTransformProperty, new ScaleTransform(1, 1));

        scrollContent.AppendChild(contentHost);
        viewer.AppendChild(scrollContent);
        root.AppendChild(viewer);
        border.AppendChild(root);
        template.VisualTree = border;
        return template;
    }

    private Point ViewerCenter()
        => _viewer is null
            ? new Point()
            : new Point(Math.Max(0, _viewer.ViewportWidth / 2), Math.Max(0, _viewer.ViewportHeight / 2));

    private void Viewer_Loaded(object sender, RoutedEventArgs e) => UpdateContentSize();

    private void Viewer_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateContentSize();

    private void Viewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!WheelZoomEnabled || _viewer is null)
        {
            return;
        }

        var anchor = e.GetPosition(_viewer);
        var next = e.Delta > 0 ? Zoom * 1.15 : Zoom / 1.15;
        ZoomAt(next, anchor);
        e.Handled = true;
    }

    private void Viewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _viewer is null)
        {
            return;
        }

        _middleDragging = true;
        _middleDragStart = e.GetPosition(_viewer);
        _middleDragHorizontalOffset = _viewer.HorizontalOffset;
        _middleDragVerticalOffset = _viewer.VerticalOffset;
        _viewer.CaptureMouse();
        e.Handled = true;
    }

    private void Viewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_middleDragging || _viewer is null || e.LeftButton is not MouseButtonState.Released)
        {
            return;
        }

        var current = e.GetPosition(_viewer);
        var delta = current - _middleDragStart;
        _viewer.ScrollToHorizontalOffset(_middleDragHorizontalOffset - delta.X);
        _viewer.ScrollToVerticalOffset(_middleDragVerticalOffset - delta.Y);
        e.Handled = true;
    }

    private void Viewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_middleDragging || _viewer is null)
        {
            return;
        }

        _middleDragging = false;
        _viewer.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ZoomAt(double value, Point anchor)
    {
        if (_viewer is null || _contentScale is null)
        {
            return;
        }

        EnsureBaseSize();
        var oldZoom = Zoom;
        var next = Math.Clamp(value, 0.25, 5.0);
        if (Math.Abs(next - oldZoom) < 0.001)
        {
            return;
        }

        var contentX = (_viewer.HorizontalOffset + anchor.X) / oldZoom;
        var contentY = (_viewer.VerticalOffset + anchor.Y) / oldZoom;
        _contentScale.ScaleX = next;
        _contentScale.ScaleY = next;
        UpdateContentSize();
        _viewer.UpdateLayout();
        _viewer.ScrollToHorizontalOffset(contentX * next - anchor.X);
        _viewer.ScrollToVerticalOffset(contentY * next - anchor.Y);
        ZoomChanged?.Invoke(this, next);
    }

    private void EnsureBaseSize()
    {
        if (_baseWidth > 0 && _baseHeight > 0)
        {
            return;
        }

        if (_viewer is null)
        {
            return;
        }

        _baseWidth = Math.Max(1, _viewer.ViewportWidth > 0 ? _viewer.ViewportWidth : _viewer.ActualWidth);
        _baseHeight = Math.Max(1, _viewer.ViewportHeight > 0 ? _viewer.ViewportHeight : _viewer.ActualHeight);
    }

    private void UpdateContentSize(bool resetBase = false)
    {
        if (_viewer is null || _contentHost is null || _scrollContent is null)
        {
            return;
        }

        if (_viewer.ViewportWidth <= 0 || _viewer.ViewportHeight <= 0)
        {
            return;
        }

        if (resetBase || Zoom <= 1.001 || _baseWidth <= 0 || _baseHeight <= 0)
        {
            _baseWidth = _viewer.ViewportWidth;
            _baseHeight = _viewer.ViewportHeight;
        }

        _contentHost.Width = _baseWidth;
        _contentHost.Height = _baseHeight;
        _scrollContent.Width = Math.Max(1, _baseWidth * Zoom);
        _scrollContent.Height = Math.Max(1, _baseHeight * Zoom);
    }

    private sealed class ActionCommand : ICommand
    {
        private readonly Action _execute;

        public ActionCommand(Action execute) => _execute = execute;

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
