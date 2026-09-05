using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VisionWorkbench.App;

/// <summary>应用内统一消息/确认弹窗，避免系统 MessageBox 与平台主题不一致。</summary>
public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message, string caption) =>
        Show(null, message, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(
        string message, string caption, MessageBoxButton buttons, MessageBoxImage image) =>
        Show(null, message, caption, buttons, image);

    public static MessageBoxResult Show(Window? owner, string message, string caption) =>
        Show(owner, message, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        var window = new Window
        {
            Title = caption,
            Width = 460,
            MinHeight = 170,
            MaxWidth = 680,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Owner = owner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
        };

        var accent = image switch
        {
            MessageBoxImage.Error or MessageBoxImage.Stop or MessageBoxImage.Hand => "#DC2626",
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => "#D97706",
            MessageBoxImage.Question => "#2563EB",
            _ => "#2563EB",
        };
        var selectedResult = MessageBoxResult.None;

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
        titlePanel.Children.Add(new Border
        {
            Width = 4,
            Height = 22,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent)),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 10, 0),
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindBrush("TextBrush"),
        });

        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FindBrush("MutedTextBrush"),
            LineHeight = 22,
            Margin = new Thickness(14, 16, 0, 20),
        };

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foreach (var (label, result, secondary) in GetButtons(buttons))
        {
            var button = new Button
            {
                Content = label,
                IsDefault = result is MessageBoxResult.OK or MessageBoxResult.Yes,
                IsCancel = result is MessageBoxResult.Cancel or MessageBoxResult.No,
                MinWidth = 76,
                Margin = new Thickness(8, 2, 0, 2),
                Padding = new Thickness(12, 5, 12, 5),
            };
            if (secondary)
            {
                button.Style = (Style)System.Windows.Application.Current.FindResource("SecondaryButton");
            }
            button.Click += (_, _) =>
            {
                selectedResult = result;
                window.DialogResult = result == MessageBoxResult.OK
                ? true
                : result == MessageBoxResult.Cancel ? false : result == MessageBoxResult.Yes;
            };
            buttonsPanel.Children.Add(button);
        }

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titlePanel, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(buttonsPanel, 2);
        content.Children.Add(titlePanel);
        content.Children.Add(body);
        content.Children.Add(buttonsPanel);

        var border = new Border
        {
            Background = FindBrush("SurfaceBrush"),
            BorderBrush = FindBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
            Child = content,
        };
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 22,
            ShadowDepth = 6,
            Direction = 270,
            Opacity = 0.18,
            Color = Color.FromRgb(51, 65, 85),
        };
        window.Content = border;
        window.Loaded += (_, _) => buttonsPanel.Children.OfType<Button>().LastOrDefault()?.Focus();
        window.ShowDialog();
        return selectedResult != MessageBoxResult.None
            ? selectedResult
            : buttons switch
            {
                MessageBoxButton.OK => MessageBoxResult.None,
                MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                MessageBoxButton.YesNo => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel,
            };
    }

    private static SolidColorBrush FindBrush(string key) =>
        (SolidColorBrush)System.Windows.Application.Current.FindResource(key);

    private static IReadOnlyList<(string Label, MessageBoxResult Result, bool Secondary)> GetButtons(
        MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OK => [("确定", MessageBoxResult.OK, false)],
        MessageBoxButton.OKCancel => [("取消", MessageBoxResult.Cancel, true), ("确定", MessageBoxResult.OK, false)],
        MessageBoxButton.YesNo => [("否", MessageBoxResult.No, true), ("是", MessageBoxResult.Yes, false)],
        MessageBoxButton.YesNoCancel => [("取消", MessageBoxResult.Cancel, true), ("否", MessageBoxResult.No, true), ("是", MessageBoxResult.Yes, false)],
        _ => [("确定", MessageBoxResult.OK, false)],
    };
}
