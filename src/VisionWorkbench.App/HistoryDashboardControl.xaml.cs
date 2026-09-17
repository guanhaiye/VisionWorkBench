using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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

    public event EventHandler? BackRequested;

    public HistoryDashboardControl()
    {
        InitializeComponent();
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
            await QueryAsync();
        };
    }

    private async void Query_Click(object sender, RoutedEventArgs e) => await QueryAsync();
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void RangeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || RangeFilter.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag?.ToString() == "today") SetRange(0);
        else if (int.TryParse(item.Tag?.ToString(), out var days)) SetRange(days);
    }

    private void SetRange(int days)
    {
        var today = DateTime.Today;
        FromDate.SelectedDate = days == 0 ? today : today.AddDays(-days + 1);
        ToDate.SelectedDate = today;
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
        foreach (var canvas in new[] { TrendCanvas, StatusCanvas, TaskCanvas, PerformanceCanvas }) canvas.Children.Clear();
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
        var canvas = TrendCanvas;
        canvas.Children.Clear();
        if (report.Trend.Count == 0) return;
        var width = Math.Max(1, canvas.ActualWidth);
        var height = Math.Max(1, canvas.ActualHeight);
        var left = 38d; var top = 22d; var bottom = height - 24d; var right = width - 8d;
        DrawAxes(canvas, left, top, right, bottom);
        var max = Math.Max(1, report.Trend.Max(x => x.Total));
        var line = ThemeBrush("AccentBrush", "#2563EB");
        var points = new List<Point>();
        for (var i = 0; i < report.Trend.Count; i++)
        {
            var x = left + (right - left) * i / Math.Max(1, report.Trend.Count - 1);
            var y = top + (bottom - top) * (1 - report.Trend[i].Total / (double)max);
            points.Add(new Point(x, y));
            var dot = new Ellipse { Width = 7, Height = 7, Fill = line, ToolTip = $"{report.Trend[i].StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}\\n数量：{report.Trend[i].Total}\\n良率：{report.Trend[i].OkRate:0.0}%" };
            canvas.Children.Add(dot); Canvas.SetLeft(dot, x - 3.5); Canvas.SetTop(dot, y - 3.5);
        }
        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using var context = geometry.Open();
            context.BeginFigure(points[0], false, false);
            foreach (var point in points.Skip(1)) context.LineTo(point, true, false);
            geometry.Freeze();
            canvas.Children.Add(new Path { Data = geometry, Stroke = line, StrokeThickness = 2 });
        }
        AddLegend(canvas, "● 检测量", line, "良率：悬停查看", ThemeBrush("SuccessBrush", "#16A34A"));
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
        var y = 8d;
        foreach (var item in values)
        {
            var text = new TextBlock { Text = $"● {item.Item1}  {item.Item2}（{item.Item2 * 100d / denominator:0.0}%）", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Item3)), FontSize = 11 };
            canvas.Children.Add(text); Canvas.SetLeft(text, 6); Canvas.SetTop(text, y); y += 19;
        }
    }

    private static Geometry CreatePie(Point center, double radius, double start, double end)
    {
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
        var rowHeight = Math.Max(16, (canvas.ActualHeight - 8) / report.Tasks.Count - 5);
        for (var i = 0; i < report.Tasks.Count; i++)
        {
            var item = report.Tasks[i]; var y = 4 + i * (rowHeight + 5);
            var name = new TextBlock { Text = item.Name, Width = 106, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.Name };
            canvas.Children.Add(name); Canvas.SetLeft(name, 2); Canvas.SetTop(name, y);
            var bar = new Rectangle { Height = rowHeight, Width = Math.Max(1, (canvas.ActualWidth - 132) * item.Total / (double)max), Fill = ThemeBrush("AccentBrush", "#2563EB"), ToolTip = $"{item.Name}\\n数量：{item.Total}\\n良率：{item.OkRate:0.0}%" };
            canvas.Children.Add(bar); Canvas.SetLeft(bar, 110); Canvas.SetTop(bar, y);
            var value = new TextBlock { Text = $"{item.Total} / {item.OkRate:0.0}%" };
            canvas.Children.Add(value); Canvas.SetLeft(value, 114 + bar.Width); Canvas.SetTop(value, y);
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
