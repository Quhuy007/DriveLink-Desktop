using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DriveLink.Ui;
namespace DriveLink;

/// <summary>
/// Hộp thoại kính thay cho MessageBox của Windows (hộp thoại hệ thống không đổi được màu).
/// Lớp đặt trùng tên MessageBox trong namespace DriveLink nên mọi lời gọi MessageBox.Show(...) sẵn có trong app tự dùng hộp thoại này.
/// </summary>
public static class MessageBox
{
    public static MessageBoxResult Show(string text) => Show((Window?)null, text);
    public static MessageBoxResult Show(string text, string caption) => Show((Window?)null, text, caption);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons) => Show((Window?)null, text, caption, buttons);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image) => Show((Window?)null, text, caption, buttons, image);
    public static MessageBoxResult Show(Window? owner, string text, string caption = "DriveLink", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var app = Application.Current;
        if (app is null) return System.Windows.MessageBox.Show(text, caption, buttons, image);
        if (!app.Dispatcher.CheckAccess()) return app.Dispatcher.Invoke(() => Show(owner, text, caption, buttons, image));
        owner ??= app.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? (app.MainWindow is { IsVisible: true } main ? main : null);
        var dialog = new GlassMessageWindow(text, caption, buttons, image);
        if (owner is { IsVisible: true } && owner != dialog) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Result;
    }
}

public sealed class GlassMessageWindow : Window
{
    public MessageBoxResult Result { get; private set; }

    public GlassMessageWindow(string text, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        Title = string.IsNullOrWhiteSpace(caption) ? "DriveLink" : caption;
        Icon = AppBrand.Icon; Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = Owner is null;
        Theme.Apply(this);
        // Đóng bằng X: chọn phương án an toàn (Hủy/Không) như hộp thoại Windows.
        Result = buttons switch { MessageBoxButton.OK => MessageBoxResult.OK, MessageBoxButton.YesNo => MessageBoxResult.No, _ => MessageBoxResult.Cancel };

        var (glyph, color) = image switch
        {
            MessageBoxImage.Error => ("", Theme.Danger),
            MessageBoxImage.Warning => ("", Theme.Warning),
            MessageBoxImage.Question => ("", Theme.Info),
            MessageBoxImage.Information => ("", Theme.Info),
            _ => ("", Theme.Accent),
        };
        var badge = new Border
        {
            Width = 44, Height = 44, CornerRadius = new(14), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0x2E, color.Color.R, color.Color.G, color.Color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, color.Color.R, color.Color.G, color.Color.B)), BorderThickness = new(1),
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var body = new StackPanel { Margin = new(16, 0, 0, 0) };
        body.Children.Add(new TextBlock { Text = Title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, FontSize = 13.5, LineHeight = 21, Margin = new(0, 8, 0, 0) });
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.Children.Add(badge); Grid.SetColumn(body, 1); top.Children.Add(body);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 22, 0, 0) };
        void Add(string label, MessageBoxResult result, bool primary, bool cancel)
        {
            var button = new Button { Content = label, MinWidth = 96, Padding = new(16, 9, 16, 9), Margin = new(10, 0, 0, 0), IsDefault = primary, IsCancel = cancel };
            if (primary) button.Style = (Style)FindResource("Primary");
            button.Click += (_, _) => { Result = result; Close(); };
            row.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageBoxButton.OKCancel: Add("Hủy", MessageBoxResult.Cancel, false, true); Add("OK", MessageBoxResult.OK, true, false); break;
            case MessageBoxButton.YesNo: Add("Không", MessageBoxResult.No, false, true); Add("Có", MessageBoxResult.Yes, true, false); break;
            case MessageBoxButton.YesNoCancel: Add("Hủy", MessageBoxResult.Cancel, false, true); Add("Không", MessageBoxResult.No, false, false); Add("Có", MessageBoxResult.Yes, true, false); break;
            default: Add("OK", MessageBoxResult.OK, true, true); break;
        }
        var panel = new StackPanel { Margin = new(24, 22, 24, 20) };
        panel.Children.Add(top); panel.Children.Add(row);
        Content = panel;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { try { Clipboard.SetText(text); } catch { } } };
    }
}
