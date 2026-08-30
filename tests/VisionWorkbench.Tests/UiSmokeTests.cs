using System.IO;
using System.Reflection;
using Path = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VisionWorkbench.App;
using VisionWorkbench.Application;
using VisionWorkbench.Contracts.Results;
using VisionWorkbench.Domain;
using VisionWorkbench.Infrastructure.Imaging;
using VisionWorkbench.Persistence;
using Xunit;

namespace VisionWorkbench.Tests;

/// <summary>
/// UI 冒烟（第四期遗留项的自动化替代）：真实 WPF 控件在 STA 线程实例化，
/// 驱动计数模式切换与叠加层绘制。构建通过只能证明编译期正确——本组用例
/// 覆盖 XAML/BAML 解析、事件挂钩与 DrawOverlay 几何（检测线/滞回带/Track/Trail）。
/// </summary>
public sealed class UiSmokeTests
{
    private static readonly BindingFlags Flags =
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static object FieldOf(object instance, string name) =>
        instance.GetType().GetField(name, Flags)!.GetValue(instance)!;

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, Flags)!.SetValue(instance, value);

    /// <summary>WPF 控件必须建在 STA 线程；异常透传回测试线程。</summary>
    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 冒烟线程超时");
        if (captured is not null)
        {
            throw captured;
        }
    }

    /// <summary>泵送 dispatcher 队列（BeginInvoke 回调、Background 优先级）。</summary>
    private static void Pump(int rounds = 4)
    {
        for (var i = 0; i < rounds; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    [Fact]
    public void TasksPage_CountingMode_Toggles_LinePanel()
    {
        RunOnSta(() =>
        {
            var page = new TasksPage(); // XAML 解析 + 事件挂钩（冒烟核心）
            var combo = (ComboBox)FieldOf(page, "ModeCombo");
            var panel = (StackPanel)FieldOf(page, "LinePanel");

            Assert.Equal(3, combo.Items.Count);
            Assert.False(panel.IsEnabled); // 默认快照：检测线编辑不可用
            Assert.Equal("0.5", ((TextBox)FieldOf(page, "LineAxText")).Text);
            Assert.Equal("0.02", ((TextBox)FieldOf(page, "HysteresisText")).Text);

            combo.SelectedIndex = 2; // 跨线 → SelectionChanged 联动
            Assert.True(panel.IsEnabled);
            combo.SelectedIndex = 1; // 动态去重
            Assert.False(panel.IsEnabled);
        });
    }

    [Fact]
    public void LivePage_RecordCompleted_Draws_Overlay_And_Updates_Counters()
    {
        RunOnSta(() =>
        {
            // dispatcher 异常兜底：绘制回调中的异常不能击穿测试宿主
            var errors = new List<Exception>();
            Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
            {
                errors.Add(e.Exception);
                e.Handled = true;
            };

            var dataDir = Path.Combine(Path.GetTempPath(), $"vw-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDir);
            var factory = VisionDbContextFactory.Create(Path.Combine(dataDir, "ui.db"));
            var run = new DetectionRunService(
                new RecordRepository(factory),
                new TempImageStore(Path.Combine(dataDir, "tmp")).Initialize(),
                logger: null, counterId: "ui-counter");

            var page = new LivePage(); // XAML 解析（新计数文本 + 清零按钮）
            page.Measure(new Size(900, 600));
            page.Arrange(new Rect(0, 0, 900, 600));
            page.UpdateLayout();
            try
            {
                var preview = (Image)FieldOf(page, "PreviewImage");
                var canvas = (Canvas)FieldOf(page, "OverlayCanvas");
                Assert.True(canvas.ActualWidth > 0 && canvas.ActualHeight > 0, "布局后画布须有实际尺寸");
                preview.Source = BitmapSource.Create(
                    800, 400, 96, 96, System.Windows.Media.PixelFormats.Bgr24,
                    null, new byte[800 * 400 * 3], 800 * 3);

                SetField(page, "_activeRecipe", new Recipe
                {
                    Name = "ui-smoke",
                    CameraProviderId = "video-file",
                    CameraDeviceId = "x.avi",
                    PluginId = "com.vision.sample-flow-counter",
                    CountingMode = CountingMode.LineCrossing,
                    CountingLine = new CountingLineConfig
                    {
                        A = new NormalizedPoint { X = 0.5, Y = 0.1 },
                        B = new NormalizedPoint { X = 0.5, Y = 0.9 },
                    },
                });
                SetField(page, "_run", run);

                var output = new AlgorithmOutput
                {
                    OutputId = "o1",
                    InputId = "i1",
                    Detections =
                    [
                        new DetectionResult
                        {
                            ClassId = "object",
                            ClassName = "物体",
                            Confidence = 0.9,
                            Box = new NormalizedRect { X = 0.2, Y = 0.4, Width = 0.08, Height = 0.08 },
                            TrackId = "1",
                        },
                    ],
                    Tracks =
                    [
                        new TrackResult
                        {
                            TrackId = "1",
                            ClassId = "object",
                            Box = new NormalizedRect { X = 0.2, Y = 0.4, Width = 0.08, Height = 0.08 },
                            Trail =
                            [
                                new NormalizedPoint { X = 0.1, Y = 0.5 },
                                new NormalizedPoint { X = 0.2, Y = 0.5 },
                                new NormalizedPoint { X = 0.3, Y = 0.5 },
                            ],
                            Age = 9,
                        },
                    ],
                    Metrics = [new MetricResult { Name = "count", Value = 1 }],
                    CountingEvents =
                    [
                        new CountingEvent
                        {
                            EventId = "e1",
                            CounterId = "ui-counter",
                            TrackId = "1",
                            Type = CountingEventType.CrossedLine,
                            Direction = "forward",
                            Delta = 1,
                        },
                    ],
                };
                run.Counting.ApplyOutput(output); // 真实计数状态：正向 +1

                var args = new RecordCompletedEventArgs
                {
                    Record = new InspectionRecordEntity { TaskId = 1 },
                    Output = output,
                    Decision = new DecisionResult { Status = DecisionStatus.Ok },
                    CountAfter = run.Counting.State.CurrentTotal,
                };
                typeof(LivePage).GetMethod("OnRecordCompleted", Flags)!.Invoke(page, [null!, args]);
                Pump();

                Assert.Empty(errors);

                // 叠加层：1 主检测线 + 2 滞回带边界（虚线）+ 1 Track 框 + Trail 折线 + ID 标签
                var lines = canvas.Children.OfType<Line>().ToList();
                Assert.Equal(3, lines.Count);
                Assert.Equal(2, lines.Count(l => l.StrokeDashArray is { Count: > 0 }));
                Assert.Single(canvas.Children.OfType<Rectangle>()); // Track 框（Track 存在时不画裸检测框）
                Assert.Contains(canvas.Children.OfType<Polyline>(), p => p.Points.Count == 3);
                Assert.Contains(canvas.Children.OfType<TextBlock>(), t => t.Text == "#1");

                // 右面板计数文本（真实 Counting.State）
                Assert.Equal("当前可见: 1", ((TextBlock)FieldOf(page, "VisibleText")).Text);
                Assert.Equal("累计: 1", ((TextBlock)FieldOf(page, "TotalText")).Text);
                Assert.Equal("正向: 1", ((TextBlock)FieldOf(page, "ForwardText")).Text);
                Assert.Equal("反向: 0", ((TextBlock)FieldOf(page, "ReverseText")).Text);
            }
            finally
            {
                run.DisposeAsync().AsTask().GetAwaiter().GetResult();
                try
                {
                    Directory.Delete(dataDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        });
    }

    [Fact]
    public void SpcPage_CanBeInstantiated()
    {
        RunOnSta(() =>
        {
            var page = new SpcPage();
            Assert.NotNull(page);
        });
    }

    [Fact]
    public void LogPage_CanBeInstantiated()
    {
        RunOnSta(() =>
        {
            var page = new LogPage();
            Assert.NotNull(page);
        });
    }

    [Fact]
    public void Shell_TcpIpNavigation_ShowsCommunicationPage()
    {
        RunOnSta(() =>
        {
            AppServices.Instance.Initialize();
            var shell = new Shell();
            var pageHost = (ContentControl)FieldOf(shell, "PageHost");
            var navigationItems = FindLogicalDescendants<ListBoxItem>(shell).ToArray();
            var tcpItem = navigationItems.Single(item => string.Equals(item.Tag?.ToString(), "communication", StringComparison.Ordinal));
            var historyItem = navigationItems.Single(item => string.Equals(item.Tag?.ToString(), "history", StringComparison.Ordinal));

            tcpItem.IsSelected = true;
            Pump();
            Assert.IsType<CommunicationPage>(pageHost.Content);

            historyItem.IsSelected = true;
            Pump();
            Assert.IsType<HistoryPage>(pageHost.Content);
            Assert.False(tcpItem.IsSelected);

            tcpItem.IsSelected = true;
            Pump();
            Assert.IsType<CommunicationPage>(pageHost.Content);
            shell.Close();
        });
    }

    [Fact]
    public void Shell_AnnotationNavigation_SeparatesDetectionAndSegmentationPlatforms()
    {
        RunOnSta(() =>
        {
            AppServices.Instance.Initialize();
            var shell = new Shell();
            var pageHost = (ContentControl)FieldOf(shell, "PageHost");
            var navigationItems = FindLogicalDescendants<ListBoxItem>(shell).ToArray();
            var detectionItem = navigationItems.Single(item =>
                string.Equals(item.Tag?.ToString(), "detection-annotation", StringComparison.Ordinal));
            var segmentationItem = navigationItems.Single(item =>
                string.Equals(item.Tag?.ToString(), "segmentation-annotation", StringComparison.Ordinal));

            Assert.IsType<WelcomePage>(pageHost.Content);
            Assert.False(detectionItem.IsSelected);
            Assert.False(segmentationItem.IsSelected);

            detectionItem.IsSelected = true;
            Pump();
            var detectionPage = Assert.IsType<DatasetAnnotationPage>(pageHost.Content);
            Assert.Equal("目标检测标注平台", ((TextBlock)FieldOf(detectionPage, "PlatformTitleText")).Text);
            Assert.Equal(Visibility.Collapsed, ((Button)FieldOf(detectionPage, "Sam1Button")).Visibility);
            Assert.Equal("导出 YOLO 检测数据集", ((Button)FieldOf(detectionPage, "ExportDatasetButton")).Content);

            segmentationItem.IsSelected = true;
            Pump();
            var segmentationPage = Assert.IsType<DatasetAnnotationPage>(pageHost.Content);
            Assert.NotSame(detectionPage, segmentationPage);
            Assert.Equal("分割标注平台", ((TextBlock)FieldOf(segmentationPage, "PlatformTitleText")).Text);
            Assert.Equal(Visibility.Visible, ((Button)FieldOf(segmentationPage, "Sam1Button")).Visibility);
            Assert.Equal("导出分割数据集", ((Button)FieldOf(segmentationPage, "ExportDatasetButton")).Content);
            Assert.False(detectionItem.IsSelected);
            shell.Close();
        });
    }

    [Fact]
    public void Shell_MinimizeButton_DoesNotMaximizeWindow()
    {
        RunOnSta(() =>
        {
            AppServices.Instance.Initialize();
            var shell = new Shell { WindowState = WindowState.Normal };
            var minimizeButton = (Button)FieldOf(shell, "MinimizeButton");

            minimizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(WindowState.Minimized, shell.WindowState);
            shell.Close();
        });
    }

    private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in FindLogicalDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    [Fact]
    public void SpcPage_Loaded_DoesNotRaiseDispatcherException()
    {
        RunOnSta(() =>
        {
            var errors = new List<Exception>();
            Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
            {
                errors.Add(e.Exception);
                e.Handled = true;
            };
            var page = new SpcPage();
            page.Measure(new Size(1000, 700));
            page.Arrange(new Rect(0, 0, 1000, 700));
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Pump(12);
            Assert.Empty(errors);
        });
    }
}
