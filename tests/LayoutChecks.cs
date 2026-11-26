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
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui-v" + AppBrand.Version)); Directory.CreateDirectory(output);
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
            if (zoom == 1)
            {
                const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
                var filesField = typeof(MainWindow).GetField("files", hidden)!;
                var loaded = (List<DriveItem>)filesField.GetValue(window)!;
                var chosen = (DriveItem)grid.Items[2];
                grid.SelectedItem = chosen;
                typeof(MainWindow).GetMethod("ClearFilesKeepingSelection", hidden)!.Invoke(window, null);
                var cleared = grid.Items.Count == 0 && grid.SelectedItem is null;
                filesField.SetValue(window, loaded);
                typeof(MainWindow).GetMethod("RenderFiles", hidden)!.Invoke(window, null);
                check(cleared && (grid.SelectedItem as DriveItem)?.Id == chosen.Id, "Reloading the file list after settings keeps the selected file");
                grid.SelectedItem = null;
                var allowed = (bool)typeof(MainWindow).GetMethod("EnsureFileSelected", hidden)!.Invoke(window, ["tải về"])!;
                var status = ((TextBlock)window.FindName("StatusLabel")).Text;
                check(!allowed && status.Contains("Chọn một tệp trong danh sách để tải về"), "Download without a selected file explains what to do instead of doing nothing");
                grid.SelectedItem = chosen;
                check((bool)typeof(MainWindow).GetMethod("EnsureFileSelected", hidden)!.Invoke(window, ["tải về"])!, "Download proceeds when a file is selected");
            }
            var image = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(output, $"main-{width:0}x{height:0}-{zoom * 100:0}.png"))) encoder.Save(stream);
            window.Close();
        }
        await ArrangeChecks(check, store, output);
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
        var message = new GlassMessageWindow("Google không cho cấp quyền xem tạm cho tệp này. DriveLink có thể tải tệp về máy rồi tải lên tài khoản đích. Tiếp tục?", "Gửi chéo qua máy", MessageBoxButton.YesNo, MessageBoxImage.Question) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
        message.Show(); message.UpdateLayout(); Save(message, Path.Combine(output, "message.png"));
        check(message.Result == MessageBoxResult.No, "Closing the glass message box without choosing keeps the safe answer"); message.Close();
        var sampleMenu = new ContextMenu();
        foreach (var label in new[] { "Đổi tên", "Tải xuống", "Tạo bản sao" }) sampleMenu.Items.Add(new MenuItem { Header = label });
        var shareMenu = new MenuItem { Header = "Chia sẻ" }; shareMenu.Items.Add(new MenuItem { Header = "Chia sẻ…" }); sampleMenu.Items.Add(shareMenu);
        sampleMenu.Items.Add(new MenuItem { Header = "Tài khoản đang dùng", IsCheckable = true, IsChecked = true });
        sampleMenu.Items.Add(new Separator()); sampleMenu.Items.Add(new MenuItem { Header = "Chuyển vào thùng rác", IsEnabled = false });
        sampleMenu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); sampleMenu.Arrange(new Rect(sampleMenu.DesiredSize)); sampleMenu.UpdateLayout();
        SaveVisual(sampleMenu, (int)sampleMenu.ActualWidth, (int)sampleMenu.ActualHeight, Path.Combine(output, "context-menu.png"));
        File.WriteAllText(Path.Combine(output, "oauth-success.html"), OAuthResultPage.Render(true, "Tài khoản minh.hoa@example.com đã được liên kết. Bạn có thể bắt đầu gửi và nhận file trong ứng dụng."));
        File.WriteAllText(Path.Combine(output, "oauth-failed.html"), OAuthResultPage.Render(false, "Yêu cầu đã bị hủy hoặc quá thời gian chờ. Bạn có thể thử lại trong ứng dụng."));
        Console.WriteLine("UI previews: " + output);
    }
    /// <summary>Bố cục bento tùy chỉnh được áp dụng khi mở cửa sổ; chế độ Sắp xếp đổi chỗ, ẩn, lưu và hủy đúng.</summary>
    private static async Task ArrangeChecks(Action<bool, string> check, SecureStore store, string output)
    {
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var layout = DriveLink.Ui.BentoLayout.Default();
        layout.FilesOnRight = true;
        layout[DriveLink.Ui.BentoLayout.Storage].Visible = false;
        layout[DriveLink.Ui.BentoLayout.Transfer].Tall = false;
        layout[DriveLink.Ui.BentoLayout.Spaces].Tall = true;
        var state = new SavedState { Accounts = [new Account { Id = "fixture", Email = "quanghuy.demo@example.com" }], Settings = new() { UiScale = 1, Layout = layout } };
        var window = new MainWindow(state, store, true, new UpdateFixture { CanInstall = false }) { Width = 1366, Height = 738, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
        window.Show(); await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        UIElement Card(string name) => (UIElement)window.FindName(name);
        check(Grid.GetColumn(Card("FilesCard")) == 1 && Grid.GetColumn(Card("SideScroll")) == 0 && Card("StorageCard").Visibility == Visibility.Collapsed
            && Grid.GetRowSpan(Card("SpacesCard")) == 2 && Grid.GetRowSpan(Card("TransferCard")) == 1 && ((DataGrid)window.FindName("FilesGrid")).ActualWidth > 500,
            "The main window opens with the saved bento layout (file list on the right, hidden and resized tiles)");
        Save(window, Path.Combine(output, "main-custom-layout.png"));

        typeof(MainWindow).GetMethod("StartArrange", hidden)!.Invoke(window, null);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var side = (Grid)window.FindName("SideGrid");
        var overlays = side.Children.OfType<Grid>().Where(g => g.Tag is string).ToList();
        check(overlays.Count == 4 && overlays.All(o => Grid.GetRow(o) == Grid.GetRow((UIElement)window.FindName(o.Tag switch { "send" => "SendCard", "transfer" => "TransferCard", "spaces" => "SpacesCard", "cross" => "CrossCard", _ => "StorageCard" }))),
            "Arrange mode covers each visible tile with a draggable frame in the same place");
        Save(window, Path.Combine(output, "main-arrange.png"));

        var change = typeof(MainWindow).GetMethod("ChangeArrange", hidden)!;
        change.Invoke(window, [new Action<DriveLink.Ui.BentoLayout>(l => { l.Swap("send", "cross"); l["storage"].Visible = true; l.FilesOnRight = false; })]);
        typeof(MainWindow).GetMethod("FinishArrange", hidden)!.Invoke(window, [false]);
        check(state.Settings.Layout.FilesOnRight && !state.Settings.Layout["storage"].Visible && Grid.GetColumn(Card("FilesCard")) == 1 && Card("StorageCard").Visibility == Visibility.Collapsed,
            "Cancel in arrange mode discards the changes");

        typeof(MainWindow).GetMethod("StartArrange", hidden)!.Invoke(window, null);
        change.Invoke(window, [new Action<DriveLink.Ui.BentoLayout>(l => { l.Swap("send", "cross"); l["storage"].Visible = true; l.FilesOnRight = false; })]);
        typeof(MainWindow).GetMethod("FinishArrange", hidden)!.Invoke(window, [true]);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var saved = store.Load().Settings.Layout.Normalize();
        check(saved.Tiles[0].Id == "cross" && saved.Tiles[3].Id == "send" && saved["storage"].Visible && !saved.FilesOnRight
            && Grid.GetColumn(Card("FilesCard")) == 0 && Card("StorageCard").Visibility == Visibility.Visible && !side.Children.OfType<Grid>().Any(g => g.Tag is string),
            "Done in arrange mode saves the new layout and removes the frames");
        window.Close();
    }

    /// <summary>Lưu ảnh một cửa sổ vào thư mục ảnh giao diện của phiên bản hiện tại.</summary>
    public static void Snapshot(Window window, string name)
    {
        window.UpdateLayout();
        Save(window, Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui-v" + AppBrand.Version)), name));
    }
    private static void Save(Window window, string path) => SaveVisual(window, (int)window.ActualWidth, (int)window.ActualHeight, path);
    private static void SaveVisual(Visual visual, int width, int height, string path)
    {
        var image = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}

