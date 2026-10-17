using DriveLink;
using DriveLink.Services;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Net;
using System.Net.Http;

static class LayoutChecks
{
    public static async Task Run(Action<bool, string> check, SecureStore store, string root)
    {
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui-v1.0.0")); Directory.CreateDirectory(output);
        foreach (var (width, height, zoom) in new[] { (1366d, 738d, 1d), (1092d, 590d, .8d), (1092d, 590d, 1.5d), (1092d, 590d, .6d) })
        {
            var state = new SavedState { Accounts = [new Account { Id = "fixture", Email = "quanghuy.demo@example.com" }], Settings = new() { UiScale = zoom } };
            var window = new MainWindow(state, store, true, new UpdateFixture { CanInstall = false }) { Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
            window.Show(); await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)window.FindName("FilesGrid"); var main = (Grid)window.FindName("UiRoot");
            var capacity = (int)Math.Floor((grid.ActualHeight - grid.ColumnHeaderHeight - 18) / grid.RowHeight);
            Console.WriteLine($"Layout {width}x{height}: actual={window.ActualWidth}x{window.ActualHeight}, table={grid.ActualWidth}x{grid.ActualHeight}, capacity={capacity}");
            check(grid.Items.Count == 45 && capacity >= (zoom <= 1 ? 9 : 1), $"File table remains usable at {width}x{height}, zoom {zoom:P0}: approximately {capacity} visible rows");
            var accountButton = (Button)window.FindName("AccountMenuButton");
            check(accountButton.ToolTip?.ToString() == "quangh******@example.com" && !accountButton.Content.ToString()!.Contains("quanghuy.demo"), "Account selector masks email and retains the linked account");
            var fixture = (DriveItem)grid.Items[0];
            var menu = (ContextMenu)typeof(MainWindow).GetMethod("BuildFileMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [fixture])!;
            check(menu.Items.OfType<MenuItem>().Select(x => (string)x.Header).SequenceEqual(new[] { "Đổi tên", "Tải xuống", "Tạo bản sao", "Chia sẻ", "Thông tin", "Chuyển vào thùng rác" }), "File menu follows the requested order");
            var info = menu.Items.OfType<MenuItem>().Single(x => (string)x.Header == "Thông tin");
            check(info.Items.OfType<MenuItem>().Select(x => (string)x.Header).SequenceEqual(new[] { "Thông tin chi tiết", "Khóa" }), "Information submenu contains only details and lock");
            var image = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(output, $"main-{width:0}x{height:0}-{zoom * 100:0}.png"))) encoder.Save(stream);
            window.Close();
        }
        UiPresentation.Scale = 1;
        var posted = 0;
        using var http = new HttpClient(new ActionHandler(request =>
        {
            var text = request.RequestUri!.AbsolutePath.EndsWith("/permissions") ? "{\"permissions\":[{\"id\":\"owner\",\"type\":\"user\",\"role\":\"owner\",\"emailAddress\":\"owner@example.com\"},{\"id\":\"inherited\",\"type\":\"user\",\"role\":\"reader\",\"emailAddress\":\"inherited@example.com\",\"permissionDetails\":[{\"inherited\":true}]}]}" : "{\"id\":\"fixture\",\"name\":\"Bản thiết kế dự án.pdf\",\"mimeType\":\"application/pdf\",\"size\":1234567,\"capabilities\":{\"canShare\":true,\"canChangeCopyRequiresWriterPermission\":true},\"owners\":[{\"emailAddress\":\"owner@example.com\",\"me\":true}]}";
            if (request.Method == HttpMethod.Post) { posted++; text = "{}"; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") });
        }));
        var drive = new DriveService(http, (_, _) => Task.FromResult("fixture"));
        var share = new FileShareWindow(drive, new() { Email = "owner@example.com" }, new() { Id = "fixture", Name = "Bản thiết kế dự án.pdf" }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
        share.Show();
        var panel = (StackPanel)((ScrollViewer)share.Content).Content;
        var submit = panel.Children.OfType<Button>().Single(x => (string)x.Content == "Gửi lời mời");
        for (var i = 0; i < 100 && !submit.IsEnabled; i++) await Task.Delay(10);
        check(submit.IsEnabled, "Share dialog loads actual capabilities before enabling invitation");
        var field = panel.Children.OfType<TextBox>().First();
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(field.BorderBrush == Brushes.Firebrick && posted == 0, "Sharing highlights a missing email and sends no request");
        field.Text = "recipient@example.com";
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && !submit.IsEnabled; i++) await Task.Delay(10);
        check(posted == 1 && field.Text == "", "Share dialog grants access once and reloads the recipient list");
        share.UpdateLayout(); Save(share, Path.Combine(output, "sharing.png")); share.Close();
        var details = new FileDetailsWindow(new() { Name = "Bản thiết kế dự án.pdf", MimeType = "application/pdf", Size = 1234567, Owners = [new() { Email = "owner@example.com" }], ContentRestrictions = [new() { ReadOnly = true, Reason = "Đã hoàn thiện" }] }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
        details.Show(); details.UpdateLayout(); Save(details, Path.Combine(output, "details.png")); details.Close();
        Console.WriteLine("UI previews: " + output);
    }
    private static void Save(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}

