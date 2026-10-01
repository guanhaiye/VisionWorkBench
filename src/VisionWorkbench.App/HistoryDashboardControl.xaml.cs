using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using VisionWorkbench.Application;

namespace VisionWorkbench.App;

public partial class HistoryDashboardControl : UserControl
{
    private sealed record TaskFilterItem(long? Id, string Name)
    {
        public override string ToString() => Name;
    }
    private DashboardReport? _report;
    private CancellationTokenSource? _queryCts;
    private bool _filtersReady;
    private bool _suppressFilterEvents;
    private bool _queryInProgress;
    private long _queryGeneration;
    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private sealed record TrendHit(string SeriesName, DashboardBucket Bucket, Point QuantityPoint, Point RatePoint);
    private readonly List<TrendHit> _trendHits = [];
    private sealed record SplitTrendHit(string SeriesName, DashboardBucket Bucket, Canvas Canvas, Point Point, bool IsRate);
    private readonly List<SplitTrendHit> _splitTrendHits = [];
    private Line? _splitHoverLine;
    private Ellipse? _splitHoverPoint;
    private Canvas? _splitHoverCanvas;
    private Line? _trendHoverLine;
    private Ellipse? _trendHoverQuantity;
    private Ellipse? _trendHoverRate;
    private ToolTip? _trendToolTip;

    public event EventHandler? BackRequested;

    public HistoryDashboardControl()
    {
        InitializeComponent();
        TrendCanvas.MouseMove += TrendCanvas_MouseMove;
        TrendCanvas.MouseLeave += TrendCanvas_MouseLeave;
        RateTrendCanvas.MouseMove += TrendCanvas_MouseMove;
        RateTrendCanvas.MouseLeave += TrendCanvas_MouseLeave;
        TaskFilter.SelectionChanged += Filter_Changed;
        StatusFilter.SelectionChanged += Filter_Changed;
        FromDate.SelectedDateChanged += DateFilter_Changed;
        ToDate.SelectedDateChanged += DateFilter_Changed;
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        Unloaded += HistoryDashboardControl_Unloaded;
        Loaded += async (_, _) =>
        {
            if (TaskFilter.Items.Count == 0)
            {
                var tasks = await AppServices.Instance.Recipes.ListAsync();
                TaskFilter.ItemsSource = new[] { new TaskFilterItem(null, "全部任务") }
                    .Concat(tasks.Select(t => new TaskFilterItem(t.Entity.Id, t.Recipe.Name))).ToArray();
                TaskFilter.SelectedIndex = 0;
            }
            SetRange(30);
            _filtersReady = true;
            await QueryAsync();
            if (IsLoaded) _autoRefreshTimer.Start();
        };
    }

    private void AutoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!_filtersReady || !IsLoaded || _queryInProgress) return;
        _ = QueryAsync();
    }

    private void HistoryDashboardControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _autoRefreshTimer.Stop();
        _queryCts?.Cancel();
    }

    private async void Query_Click(object sender, RoutedEventArgs e) => await QueryAsync();
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void RangeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || RangeFilter.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag?.ToString() == "today") SetRange(0);
        else if (int.TryParse(item.Tag?.ToString(), out var days)) SetRange(days);
        if (_filtersReady && !_suppressFilterEvents)
            _ = QueryAsync();
    }

    private void SetRange(int days)
    {
        var today = DateTime.Today;
        _suppressFilterEvents = true;
        try
        {
            FromDate.SelectedDate = days == 0 ? today : today.AddDays(-days + 1);
            ToDate.SelectedDate = today;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filtersReady || _suppressFilterEvents || !IsLoaded) return;
        await QueryAsync();
    }

    private async void DateFilter_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_filtersReady || _suppressFilterEvents || !IsLoaded) return;
        await QueryAsync();
    }

    private async Task QueryAsync()
    {
        if (!AppServices.Instance.Settings.EnableHistory)
        {
            QueryStatusText.Text = "历史记录已关闭，请在系统设置中开启";
            ClearReport();
            return;
        }
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = new CancellationTokenSource();
        var ct = _queryCts.Token;
        var fromLocal = (FromDate.SelectedDate ?? DateTime.Today).Date;
        var toLocalExclusive = (ToDate.SelectedDate ?? DateTime.Today).Date.AddDays(1);
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(fromLocal, DateTimeKind.Unspecified));
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(toLocalExclusive, DateTimeKind.Unspecified));
        var status = (StatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var taskId = (TaskFilter.SelectedItem as TaskFilterItem)?.Id;
        QueryStatusText.Text = "查询中…";
        var queryGeneration = Interlocked.Increment(ref _queryGeneration);
        _queryInProgress = true;
        try
        {
            var report = await AppServices.Instance.Reporting.BuildDashboardAsync(
                new DashboardQuery(taskId, status == "全部" ? null : status, fromUtc, toUtc), ct);
            if (ct.IsCancellationRequested) return;
            _report = report;
            RenderReport(report);
            QueryStatusText.Text = report.Total == 0 ? "暂无终态数据" : $"已更新 {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppServices.Instance.LoggerFactory.CreateLogger<HistoryDashboardControl>()
                .LogError(ex, "历史看板查询失败");
            QueryStatusText.Text = $"查询失败：{ex.Message}";
            ClearReport();
        }
        finally
        {
            if (queryGeneration == Volatile.Read(ref _queryGeneration))
                _queryInProgress = false;
        }
    }

    private void RenderReport(DashboardReport report)
    {
        TotalText.Text = report.Total.ToString(CultureInfo.InvariantCulture);
        OkText.Text = report.Ok.ToString(CultureInfo.InvariantCulture);
        NgText.Text = report.Ng.ToString(CultureInfo.InvariantCulture);
        ReviewText.Text = report.Review.ToString(CultureInfo.InvariantCulture);
        RateText.Text = $"{report.OkRate:0.0}%";
        AverageText.Text = $"{report.Performance.AverageMs:0.#} ms";
        P95Text.Text = $"{report.Performance.P95Ms:0.#} ms";
        OtherText.Text = $"{report.Error} / {report.Processing}";
        DrawTrend(report);
        DrawStatus(report);
        DrawTasks(report);
        DrawPerformance(report);
        SopSummaryCard.Visibility = report.Sop is null ? Visibility.Collapsed : Visibility.Visible;
        if (report.Sop is { } sop)
        {
            SopSummaryText.Text = $"产品周期 {sop.ProductCycles} · OK {sop.Ok} · 超时 {sop.Timeout} · 顺序错误 {sop.WrongOrder} · 待复核 {sop.Review} · 中止 {sop.Aborted}";
            SopFailureText.Text = report.SopFailures.Count == 0
                ? "暂无步骤失败"
                : "步骤失败 Top 10：" + string.Join("、", report.SopFailures.Select(x => $"{x.Name}（{x.Total}）"));
        }
    }

    private void ClearReport()
    {
        _report = null;
        foreach (var text in new[] { TotalText, OkText, NgText, ReviewText, RateText, AverageText, P95Text, OtherText }) text.Text = "0";
        foreach (var canvas in new[] { TrendCanvas, RateTrendCanvas, StatusCanvas, TaskCanvas, PerformanceCanvas }) canvas.Children.Clear();
        SopSummaryCard.Visibility = Visibility.Collapsed;
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_report is not null) RenderReport(_report);
    }

    private Brush ThemeBrush(string key, string fallback)
        => (TryFindResource(key) as Brush) ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));

    private void DrawTrend(DashboardReport report)
    {
        _trendHits.Clear();
        _splitTrendHits.Clear();
        HideTrendHover();
        HideSplitTrendHover();

        IReadOnlyList<DashboardTrendSeries> series = report.TrendSeries.Count > 0
            ? report.TrendSeries
            : [new DashboardTrendSeries(0, "全部任务", report.Trend)];
        DrawSplitTrendChart(report, series, TrendCanvas, rate: false);
        DrawSplitTrendChart(report, series, RateTrendCanvas, rate: true);
    }

    private void DrawSplitTrendChart(
        DashboardReport report,
        IReadOnlyList<DashboardTrendSeries> series,
        Canvas canvas,
        bool rate)
    {
        canvas.Children.Clear();
        if (report.Trend.Count == 0) return;
        var width = Math.Max(1, canvas.ActualWidth);
        var height = Math.Max(1, canvas.ActualHeight);
        var left = 52d;
        var top = 24d;
        var bottom = height - 30d;
        var right = width - 12d;
        var max = rate
            ? 100
            : Math.Max(1, series.SelectMany(item => item.Points).Max(point => point.Total));
        DrawSplitAxes(canvas, report, series[0].Points, left, top, right, bottom, max, rate);

        var palette = new[] { "#3B82F6", "#F97316", "#A855F7", "#14B8A6", "#EAB308", "#EC4899", "#22C55E", "#EF4444" };
        var legendLeft = 6d;
        for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
        {
            var item = series[seriesIndex];
            var color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[seriesIndex % palette.Length]));
            var points = new List<Point>();
            for (var pointIndex = 0; pointIndex < item.Points.Count; pointIndex++)
            {
                var bucket = item.Points[pointIndex];
                var x = left + (right - left) * pointIndex / Math.Max(1, item.Points.Count - 1);
                var value = rate ? bucket.OkRate : bucket.Total;
                var y = top + (bottom - top) * (1 - value / max);
                var point = new Point(x, y);
                points.Add(point);
                _splitTrendHits.Add(new SplitTrendHit(item.Name, bucket, canvas, point, rate));
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = color,
                    IsHitTestVisible = false,
                };
                canvas.Children.Add(dot);
                Canvas.SetLeft(dot, x - 3);
                Canvas.SetTop(dot, y - 3);
            }
            AddSplitTrendPath(canvas, points, color);

            var legend = new TextBlock
            {
                Text = $"● {item.Name}",
                Foreground = color,
                FontSize = 10,
                Width = 118,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Name,
            };
            canvas.Children.Add(legend);
            Canvas.SetLeft(legend, legendLeft);
            Canvas.SetTop(legend, 2);
            legendLeft += 122;
        }

        var chartLabel = new TextBlock
        {
            Text = rate ? "良率（%）" : "检测量（次）",
            Foreground = ThemeBrush("MutedTextBrush", "#AAB4C3"),
            FontSize = 10,
            IsHitTestVisible = false,
        };
        canvas.Children.Add(chartLabel);
        Canvas.SetLeft(chartLabel, Math.Max(6, right - 74));
        Canvas.SetTop(chartLabel, 2);
    }

    private void DrawSplitAxes(
        Canvas canvas,
        DashboardReport report,
        IReadOnlyList<DashboardBucket> points,
        double left,
        double top,
        double right,
        double bottom,
        double max,
        bool rate)
    {
        var axisBrush = ThemeBrush("MutedTextBrush", "#AAB4C3");
        var gridBrush = ThemeBrush("BorderBrush", "#3A4658");
        DrawAxes(canvas, left, top, right, bottom);
        const int yTicks = 5;
        for (var index = 0; index <= yTicks; index++)
        {
            var ratio = index / (double)yTicks;
            var y = bottom - (bottom - top) * ratio;
            canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = right,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = index == 0 ? 1.2 : .6,
                Opacity = index == 0 ? 1 : .65,
                IsHitTestVisible = false,
            });
            AddTrendText(canvas,
                rate ? $"{ratio * 100:0}%" : Math.Round(max * ratio).ToString(CultureInfo.InvariantCulture),
                axisBrush, left - 48, y - 8, 44, TextAlignment.Right);
        }

        var labelCount = Math.Min(points.Count, Math.Max(2, (int)((right - left) / 90)));
        var dateFormat = report.ToUtc - report.FromUtc <= TimeSpan.FromDays(2) ? "MM-dd HH:mm" : "MM-dd";
        for (var index = 0; index < labelCount; index++)
        {
            var pointIndex = labelCount == 1
                ? 0
                : (int)Math.Round(index * (points.Count - 1d) / (labelCount - 1));
            var x = left + (right - left) * pointIndex / Math.Max(1, points.Count - 1);
            AddTrendText(canvas,
                points[pointIndex].StartUtc.ToLocalTime().ToString(dateFormat, CultureInfo.InvariantCulture),
                axisBrush, x - 42, bottom + 5, 84, TextAlignment.Center);
        }
    }

    private static void AddSplitTrendPath(Canvas canvas, IReadOnlyList<Point> points, Brush stroke)
    {
        if (points.Count < 2) return;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(points[0], false, false);
        foreach (var point in points.Skip(1)) context.LineTo(point, true, false);
        geometry.Freeze();
        canvas.Children.Add(new Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        });
    }

    private void DrawLegacyTrend(DashboardReport report)
    {
        var canvas = TrendCanvas;
        canvas.Children.Clear();
        _trendHits.Clear();
        HideTrendHover();
        if (report.Trend.Count == 0) return;
        if (report.TrendSeries.Count > 1)
        {
            DrawMultiTaskTrend(report);
            return;
        }
        var width = Math.Max(1, canvas.ActualWidth);
        var height = Math.Max(1, canvas.ActualHeight);
        var left = 52d; var top = 30d; var bottom = height - 38d; var right = width - 58d;
        DrawAxes(canvas, left, top, right, bottom);
        var max = Math.Max(1, report.Trend.Max(x => x.Total));
        var line = ThemeBrush("AccentBrush", "#2563EB");
        var rateLine = ThemeBrush("SuccessBrush", "#16A34A");
        var points = new List<Point>();
        var ratePoints = new List<Point>();
        for (var i = 0; i < report.Trend.Count; i++)
        {
            var x = left + (right - left) * i / Math.Max(1, report.Trend.Count - 1);
            var y = top + (bottom - top) * (1 - report.Trend[i].Total / (double)max);
            var rateY = top + (bottom - top) * (1 - report.Trend[i].OkRate / 100d);
            points.Add(new Point(x, y));
            ratePoints.Add(new Point(x, rateY));
            _trendHits.Add(new TrendHit("全部任务", report.Trend[i], points[^1], ratePoints[^1]));
            var dot = new Ellipse { Width = 7, Height = 7, Fill = line, ToolTip = $"{report.Trend[i].StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}\\n数量：{report.Trend[i].Total}\\n良率：{report.Trend[i].OkRate:0.0}%" };
            canvas.Children.Add(dot); Canvas.SetLeft(dot, x - 3.5); Canvas.SetTop(dot, y - 3.5);
        }
        DrawTrendLabels(canvas, report, left, top, right, bottom, max);
        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using var context = geometry.Open();
            context.BeginFigure(points[0], false, false);
            foreach (var point in points.Skip(1)) context.LineTo(point, true, false);
            geometry.Freeze();
            canvas.Children.Add(new Path { Data = geometry, Stroke = line, StrokeThickness = 2 });

            var rateGeometry = new StreamGeometry();
            using var rateContext = rateGeometry.Open();
            rateContext.BeginFigure(ratePoints[0], false, false);
            foreach (var point in ratePoints.Skip(1)) rateContext.LineTo(point, true, false);
            rateGeometry.Freeze();
            canvas.Children.Add(new Path { Data = rateGeometry, Stroke = rateLine, StrokeThickness = 2 });
        }
        AddLegend(canvas, "● 检测量", line, "良率：悬停查看", ThemeBrush("SuccessBrush", "#16A34A"));
    }

    private void DrawMultiTaskTrend(DashboardReport report)
    {
        var canvas = TrendCanvas;
        var width = Math.Max(1, canvas.ActualWidth);
        var height = Math.Max(1, canvas.ActualHeight);
        var left = 52d;
        var top = 30d;
        var bottom = height - 38d;
        var right = width - 58d;
        var max = Math.Max(1, report.TrendSeries.SelectMany(series => series.Points).Max(point => point.Total));
        var palette = new[] { "#3B82F6", "#F97316", "#A855F7", "#14B8A6", "#EAB308", "#EC4899", "#22C55E", "#EF4444" };

        DrawAxes(canvas, left, top, right, bottom);
        DrawTrendLabels(canvas, report, left, top, right, bottom, max);

        var legendLeft = 6d;
        for (var seriesIndex = 0; seriesIndex < report.TrendSeries.Count; seriesIndex++)
        {
            var series = report.TrendSeries[seriesIndex];
            if (series.Points.Count == 0) continue;
            var color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[seriesIndex % palette.Length]));
            var quantityPoints = new List<Point>();
            var ratePoints = new List<Point>();
            for (var pointIndex = 0; pointIndex < series.Points.Count; pointIndex++)
            {
                var bucket = series.Points[pointIndex];
                var x = left + (right - left) * pointIndex / Math.Max(1, series.Points.Count - 1);
                var quantityY = top + (bottom - top) * (1 - bucket.Total / (double)max);
                var rateY = top + (bottom - top) * (1 - bucket.OkRate / 100d);
                quantityPoints.Add(new Point(x, quantityY));
                ratePoints.Add(new Point(x, rateY));
                _trendHits.Add(new TrendHit(series.Name, bucket, quantityPoints[^1], ratePoints[^1]));
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = color,
                    IsHitTestVisible = false,
                };
                canvas.Children.Add(dot);
                Canvas.SetLeft(dot, x - 3);
                Canvas.SetTop(dot, quantityY - 3);
            }

            AddTrendPath(canvas, quantityPoints, color, dashed: false);
            AddTrendPath(canvas, ratePoints, color, dashed: true);

            var legend = new TextBlock
            {
                Text = $"● {series.Name}",
                Foreground = color,
                FontSize = 10,
                Width = 118,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = series.Name,
            };
            canvas.Children.Add(legend);
            Canvas.SetLeft(legend, legendLeft);
            Canvas.SetTop(legend, 2);
            legendLeft += 122;
        }

        var modeText = new TextBlock
        {
            Text = "实线：检测量  虚线：良率",
            Foreground = ThemeBrush("MutedTextBrush", "#AAB4C3"),
            FontSize = 10,
            IsHitTestVisible = false,
        };
        canvas.Children.Add(modeText);
        Canvas.SetLeft(modeText, Math.Max(6, right - 150));
        Canvas.SetTop(modeText, 2);
    }

    private static void AddTrendPath(Canvas canvas, IReadOnlyList<Point> points, Brush stroke, bool dashed)
    {
        if (points.Count < 2) return;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(points[0], false, false);
        foreach (var point in points.Skip(1)) context.LineTo(point, true, false);
        geometry.Freeze();
        var path = new Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = dashed ? 1.5 : 2,
            Opacity = dashed ? .75 : 1,
            IsHitTestVisible = false,
        };
        if (dashed) path.StrokeDashArray = [4, 3];
        canvas.Children.Add(path);
    }

    private void DrawTrendLabels(
        Canvas canvas,
        DashboardReport report,
        double left,
        double top,
        double right,
        double bottom,
        int maxQuantity)
    {
        var axisBrush = ThemeBrush("MutedTextBrush", "#AAB4C3");
        var gridBrush = ThemeBrush("BorderBrush", "#3A4658");
        const int yTicks = 5;
        for (var index = 0; index <= yTicks; index++)
        {
            var ratio = index / (double)yTicks;
            var y = bottom - (bottom - top) * ratio;
            canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = right,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = index == 0 ? 1.2 : 0.6,
                Opacity = index == 0 ? 1 : .65,
                IsHitTestVisible = false,
            });
            AddTrendText(canvas, Math.Round(maxQuantity * ratio).ToString(CultureInfo.InvariantCulture),
                axisBrush, left - 48, y - 8, 44, TextAlignment.Right);
            AddTrendText(canvas, $"{ratio * 100:0}%", axisBrush,
                right + 5, y - 8, 48, TextAlignment.Left);
        }

        var labelCount = Math.Min(report.Trend.Count, Math.Max(2, (int)((right - left) / 90)));
        var dateFormat = report.ToUtc - report.FromUtc <= TimeSpan.FromDays(2)
            ? "MM-dd HH:mm"
            : "MM-dd";
        for (var index = 0; index < labelCount; index++)
        {
            var pointIndex = labelCount == 1
                ? 0
                : (int)Math.Round(index * (report.Trend.Count - 1d) / (labelCount - 1));
            var x = left + (right - left) * pointIndex / Math.Max(1, report.Trend.Count - 1);
            AddTrendText(canvas,
                report.Trend[pointIndex].StartUtc.ToLocalTime().ToString(dateFormat, CultureInfo.InvariantCulture),
                axisBrush, x - 42, bottom + 5, 84, TextAlignment.Center);
        }
    }

    private static void AddTrendText(
        Canvas canvas,
        string text,
        Brush foreground,
        double left,
        double top,
        double width,
        TextAlignment alignment)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = 10,
            Width = width,
            TextAlignment = alignment,
            IsHitTestVisible = false,
        };
        canvas.Children.Add(label);
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
    }

    private void ShowSplitTrendHover(Canvas canvas, Point position)
    {
        var candidates = _splitTrendHits.Where(hit => ReferenceEquals(hit.Canvas, canvas)).ToArray();
        if (candidates.Length == 0) return;
        var hit = candidates.MinBy(item => Math.Abs(item.Point.X - position.X));
        if (hit is null) return;

        if (!ReferenceEquals(_splitHoverCanvas, canvas))
            HideSplitTrendHover();
        if (_splitHoverLine is null)
        {
            _splitHoverCanvas = canvas;
            _splitHoverLine = new Line
            {
                Stroke = ThemeBrush("MutedTextBrush", "#AAB4C3"),
                StrokeThickness = 1,
                StrokeDashArray = [3, 3],
                IsHitTestVisible = false,
            };
            _splitHoverPoint = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = ThemeBrush("AccentBrush", "#2563EB"),
                Stroke = Brushes.White,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            canvas.Children.Add(_splitHoverLine);
            canvas.Children.Add(_splitHoverPoint);
        }

        _splitHoverLine.X1 = _splitHoverLine.X2 = hit.Point.X;
        _splitHoverLine.Y1 = 24;
        _splitHoverLine.Y2 = Math.Max(24, canvas.ActualHeight - 30);
        Canvas.SetLeft(_splitHoverPoint, hit.Point.X - 4.5);
        Canvas.SetTop(_splitHoverPoint, hit.Point.Y - 4.5);
        _trendToolTip ??= new ToolTip
        {
            PlacementTarget = canvas,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            StaysOpen = true,
        };
        _trendToolTip.PlacementTarget = canvas;
        var sameBucketHits = candidates
            .Where(item => Math.Abs(item.Point.X - hit.Point.X) <= 1)
            .OrderBy(item => item.SeriesName, StringComparer.Ordinal)
            .ToArray();
        var tooltipLines = sameBucketHits
            .Select(item => item.IsRate
                ? $"任务：{item.SeriesName}  良率：{item.Bucket.OkRate:0.0}%"
                : $"任务：{item.SeriesName}  检测量：{item.Bucket.Total}")
            .ToArray();
        _trendToolTip.Content = string.Join(Environment.NewLine, tooltipLines);
        _trendToolTip.HorizontalOffset = Math.Clamp(position.X + 14, 0, Math.Max(0, canvas.ActualWidth - 210));
        _trendToolTip.VerticalOffset = Math.Clamp(position.Y + 14, 0, Math.Max(0, canvas.ActualHeight - 100));
        _trendToolTip.IsOpen = true;
    }

    private void HideSplitTrendHover()
    {
        if (_splitHoverCanvas is not null)
        {
            if (_splitHoverLine is not null) _splitHoverCanvas.Children.Remove(_splitHoverLine);
            if (_splitHoverPoint is not null) _splitHoverCanvas.Children.Remove(_splitHoverPoint);
        }
        _splitHoverLine = null;
        _splitHoverPoint = null;
        _splitHoverCanvas = null;
        if (_trendToolTip is not null) _trendToolTip.IsOpen = false;
    }

    private void TrendCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Canvas splitCanvas && _splitTrendHits.Count > 0)
        {
            ShowSplitTrendHover(splitCanvas, e.GetPosition(splitCanvas));
            return;
        }
        if (_trendHits.Count == 0) return;
        var position = e.GetPosition(TrendCanvas);
        var hit = _trendHits.MinBy(item => Math.Abs(item.QuantityPoint.X - position.X));
        if (hit is null) return;

        if (_trendHoverLine is null)
        {
            _trendHoverLine = new Line
            {
                Stroke = ThemeBrush("MutedTextBrush", "#AAB4C3"),
                StrokeThickness = 1,
                StrokeDashArray = [3, 3],
                IsHitTestVisible = false,
            };
            _trendHoverQuantity = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = ThemeBrush("AccentBrush", "#2563EB"),
                Stroke = Brushes.White,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            _trendHoverRate = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = ThemeBrush("SuccessBrush", "#16A34A"),
                Stroke = Brushes.White,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            TrendCanvas.Children.Add(_trendHoverLine);
            TrendCanvas.Children.Add(_trendHoverQuantity);
            TrendCanvas.Children.Add(_trendHoverRate);
        }

        _trendHoverLine.X1 = _trendHoverLine.X2 = hit.QuantityPoint.X;
        _trendHoverLine.Y1 = 30;
        _trendHoverLine.Y2 = Math.Max(30, TrendCanvas.ActualHeight - 38);
        Canvas.SetLeft(_trendHoverQuantity, hit.QuantityPoint.X - 4.5);
        Canvas.SetTop(_trendHoverQuantity, hit.QuantityPoint.Y - 4.5);
        Canvas.SetLeft(_trendHoverRate, hit.RatePoint.X - 4.5);
        Canvas.SetTop(_trendHoverRate, hit.RatePoint.Y - 4.5);

        _trendToolTip ??= new ToolTip
        {
            PlacementTarget = TrendCanvas,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            StaysOpen = true,
        };
        _trendToolTip.Content = string.Join(Environment.NewLine,
            $"任务：{hit.SeriesName}",
            $"时间：{hit.Bucket.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}",
            $"检测量：{hit.Bucket.Total}",
            $"良率：{hit.Bucket.OkRate:0.0}%",
            $"OK：{hit.Bucket.Ok}  NG：{hit.Bucket.Ng}  待复核：{hit.Bucket.Review}");
        _trendToolTip.HorizontalOffset = Math.Clamp(position.X + 14, 0, Math.Max(0, TrendCanvas.ActualWidth - 210));
        _trendToolTip.VerticalOffset = Math.Clamp(position.Y + 14, 0, Math.Max(0, TrendCanvas.ActualHeight - 100));
        _trendToolTip.IsOpen = true;
    }

    private void TrendCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HideSplitTrendHover();
        HideTrendHover();
    }

    private void HideTrendHover()
    {
        if (_trendHoverLine is not null)
        {
            TrendCanvas.Children.Remove(_trendHoverLine);
            TrendCanvas.Children.Remove(_trendHoverQuantity);
            TrendCanvas.Children.Remove(_trendHoverRate);
            _trendHoverLine = null;
            _trendHoverQuantity = null;
            _trendHoverRate = null;
        }
        if (_trendToolTip is not null)
            _trendToolTip.IsOpen = false;
    }

    private void DrawStatus(DashboardReport report)
    {
        var canvas = StatusCanvas;
        canvas.Children.Clear();
        var values = new[] { ("OK", report.Ok, "#16A34A"), ("NG", report.Ng, "#DC2626"), ("待复核", report.Review, "#D97706") };
        var denominator = Math.Max(1, values.Sum(x => x.Item2));
        var radius = Math.Max(12, Math.Min(canvas.ActualWidth, canvas.ActualHeight) * .31);
        var center = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2 + 8);
        var angle = -Math.PI / 2;
        foreach (var item in values)
        {
            var sweep = item.Item2 / (double)denominator * Math.PI * 2;
            var slice = new Path
            {
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Item3)),
                Data = CreatePie(center, radius, angle, angle + sweep),
                ToolTip = $"{item.Item1}：{item.Item2}（{item.Item2 * 100d / denominator:0.0}%）",
            };
            canvas.Children.Add(slice);
            angle += sweep;
        }
        var hole = new Ellipse { Width = radius * .78, Height = radius * .78, Fill = ThemeBrush("SurfaceBrush", "#FFFFFF") };
        canvas.Children.Add(hole); Canvas.SetLeft(hole, center.X - hole.Width / 2); Canvas.SetTop(hole, center.Y - hole.Height / 2);
        var total = values.Sum(item => item.Item2);
        var centerText = new TextBlock
        {
            Text = $"{total}\n总数",
            Foreground = ThemeBrush("TextBrush", "#E5E7EB"),
            FontSize = Math.Clamp(radius * .16, 14, 24),
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Width = hole.Width,
            Height = hole.Height,
            Padding = new Thickness(0, hole.Height * .25, 0, 0),
            IsHitTestVisible = false,
        };
        canvas.Children.Add(centerText);
        Canvas.SetLeft(centerText, center.X - centerText.Width / 2);
        Canvas.SetTop(centerText, center.Y - centerText.Height / 2);
        var y = 8d;
        foreach (var item in values)
        {
            var text = new TextBlock { Text = $"● {item.Item1}  {item.Item2}（{item.Item2 * 100d / denominator:0.0}%）", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Item3)), FontSize = 11 };
            canvas.Children.Add(text); Canvas.SetLeft(text, 6); Canvas.SetTop(text, y); y += 19;
        }
    }

    private static Geometry CreatePie(Point center, double radius, double start, double end)
    {
        if (end - start >= Math.PI * 2 - 0.0001)
            return new EllipseGeometry(center, radius, radius);
        var startPoint = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
        var endPoint = new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end));
        var figure = new PathFigure { StartPoint = center, IsClosed = true };
        figure.Segments.Add(new LineSegment(startPoint, true));
        figure.Segments.Add(new ArcSegment(endPoint, new Size(radius, radius), 0, end - start >= Math.PI, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { figure });
    }

    private void DrawTasks(DashboardReport report)
    {
        var canvas = TaskCanvas; canvas.Children.Clear();
        if (report.Tasks.Count == 0) return;
        var max = Math.Max(1, report.Tasks.Max(x => x.Total));
        var rowHeight = Math.Min(36, Math.Max(16, (canvas.ActualHeight - 8) / report.Tasks.Count - 5));
        const double labelLeft = 110;
        const double valueLabelWidth = 112;
        var availableBarWidth = Math.Max(1, canvas.ActualWidth - labelLeft - valueLabelWidth - 12);
        var barMaxWidth = Math.Max(1, availableBarWidth * 0.82);
        for (var i = 0; i < report.Tasks.Count; i++)
        {
            var item = report.Tasks[i]; var y = 4 + i * (rowHeight + 5);
            var name = new TextBlock { Text = item.Name, Width = 106, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.Name };
            canvas.Children.Add(name); Canvas.SetLeft(name, 2); Canvas.SetTop(name, y);
            var bar = new Rectangle
            {
                Height = rowHeight,
                Width = Math.Max(1, (canvas.ActualWidth - 132) * item.Total / (double)max),
                Fill = ThemeBrush("AccentBrush", "#2563EB"),
                ToolTip = $"{item.Name}{Environment.NewLine}数量：{item.Total}{Environment.NewLine}良率：{item.OkRate:0.0}%",
            };
            bar.Width = Math.Max(1, barMaxWidth * item.Total / max);
            canvas.Children.Add(bar); Canvas.SetLeft(bar, labelLeft); Canvas.SetTop(bar, y);
            var value = new TextBlock
            {
                Text = $"{item.Total} / {item.OkRate:0.0}%",
                Width = valueLabelWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = $"{item.Total} / {item.OkRate:0.0}%",
            };
            canvas.Children.Add(value);
            Canvas.SetLeft(value, Math.Min(labelLeft + bar.Width + 4, canvas.ActualWidth - valueLabelWidth - 4));
            Canvas.SetTop(value, y);
        }
    }

    private void DrawPerformance(DashboardReport report)
    {
        var canvas = PerformanceCanvas; canvas.Children.Clear();
        var values = new[] { ("平均", report.Performance.AverageMs), ("P50", report.Performance.P50Ms), ("P95", report.Performance.P95Ms), ("P99", report.Performance.P99Ms) };
        var max = Math.Max(1, values.Max(x => x.Item2)); var barWidth = Math.Max(20, (canvas.ActualWidth - 34) / values.Length - 10);
        for (var i = 0; i < values.Length; i++)
        {
            var x = 8 + i * (barWidth + 10); var barHeight = Math.Max(1, (canvas.ActualHeight - 34) * values[i].Item2 / max);
            var bar = new Rectangle { Width = barWidth, Height = barHeight, Fill = ThemeBrush("AccentBrush", "#2563EB"), ToolTip = $"{values[i].Item1}：{values[i].Item2:0.#} ms" };
            canvas.Children.Add(bar); Canvas.SetLeft(bar, x); Canvas.SetTop(bar, canvas.ActualHeight - 25 - barHeight);
            var valueText = new TextBlock
            {
                Text = $"{values[i].Item2:0.#} ms",
                Width = barWidth,
                TextAlignment = TextAlignment.Center,
                Foreground = ThemeBrush("TextBrush", "#E5E7EB"),
                FontSize = 10,
                IsHitTestVisible = false,
            };
            canvas.Children.Add(valueText);
            Canvas.SetLeft(valueText, x);
            Canvas.SetTop(valueText, Math.Max(0, canvas.ActualHeight - 25 - barHeight - 18));
            var label = new TextBlock { Text = values[i].Item1, Width = barWidth, TextAlignment = TextAlignment.Center };
            canvas.Children.Add(label); Canvas.SetLeft(label, x); Canvas.SetTop(label, canvas.ActualHeight - 22);
        }
    }

    private void DrawAxes(Canvas canvas, double left, double top, double right, double bottom)
    {
        var brush = ThemeBrush("BorderBrush", "#B8C4D3");
        canvas.Children.Add(new Line { X1 = left, Y1 = top, X2 = left, Y2 = bottom, Stroke = brush });
        canvas.Children.Add(new Line { X1 = left, Y1 = bottom, X2 = right, Y2 = bottom, Stroke = brush });
    }

    private static void AddLegend(Canvas canvas, string first, Brush firstBrush, string second, Brush secondBrush)
    {
        var a = new TextBlock { Text = first, Foreground = firstBrush, FontSize = 11 };
        var b = new TextBlock { Text = second, Foreground = secondBrush, FontSize = 11 };
        canvas.Children.Add(a); canvas.Children.Add(b); Canvas.SetLeft(a, 6); Canvas.SetTop(a, 2); Canvas.SetLeft(b, 100); Canvas.SetTop(b, 2);
    }
}
