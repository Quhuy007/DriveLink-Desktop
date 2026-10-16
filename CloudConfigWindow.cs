using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using DriveLink.Services;
namespace DriveLink;

public sealed class CloudConfigWindow : Window
{
    private readonly SavedState state;
    private readonly Action<OAuthConfig, CloudRevision> apply;
    private readonly TextBox label = new();
    private readonly PasswordBox unlock = new(), publishPassword = new(), confirmPassword = new();
    private readonly TextBlock active = new(), preview = new(), status = new(), historyCount = new();
    private readonly Border statusCard = new() { CornerRadius = new(10), Padding = new(14), Margin = new(0, 0, 0, 8), BorderThickness = new(1) };
    private readonly Func<SupabaseConfigService> createService;
    private readonly ListBox versions = new() { MinHeight = 120, MaxHeight = 210 };
    private readonly List<CloudRevision> history = [];
    private readonly StackPanel admin = new();
    private readonly TabControl tabs = new();
    private readonly TabItem adminTab;
    private readonly Button download = new(), refresh = new(), cancel = new(), more = new();
    private SupabaseConfigService? service;
    private CancellationTokenSource? operation;
    private readonly CancellationTokenSource lifetime = new();
    private CloudRevision? current;
    private string? selectedJson;
    private bool busy, adminRequested, hasMore;
    private int historyOffset;
    public CloudConfigWindow(SavedState state, SecureStore store, Action<OAuthConfig, CloudRevision> apply, Func<SupabaseConfigService>? serviceFactory = null, bool autoLoad = true)
    {
        this.state = state; this.apply = apply;
        createService = serviceFactory ?? (() => new SupabaseConfigService(CloudDefaults.Read() ?? SupabaseConfigService.ValidateConnection(state.Cloud.ProjectUrl, state.Cloud.PublishableKey)));
        Title = "Cloud lấy OAuth — DriveLink · Quang Huy"; Icon = AppBrand.Icon;
        Width = 780; Height = Math.Min(850, SystemParameters.WorkArea.Height); MinWidth = 620; MinHeight = Math.Min(600, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(245, 247, 251));
        var root = new Grid { Margin = new(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Cloud lấy OAuth", FontSize = 26, FontWeight = FontWeights.SemiBold });
        header.Children.Add(Hint("Cloud sẵn sàng nhận cấu hình. Chỉ cần nhập mật khẩu OAuth để tải và mở khóa; không cần đăng nhập reader.", new(0, 10, 0, 14)));
        status.TextWrapping = TextWrapping.Wrap; status.FontSize = 15; status.FontWeight = FontWeights.SemiBold; statusCard.Child = status; header.Children.Add(statusCard);
        root.Children.Add(header); tabs.Margin = new(0, 12, 0, 12); Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var receiving = new StackPanel { Margin = new(16) };
        receiving.Children.Add(new TextBlock { Text = "Cấu hình OAuth", FontSize = 20, FontWeight = FontWeights.SemiBold });
        active.Text = "Bấm Làm mới để kiểm tra cấu hình."; active.TextWrapping = TextWrapping.Wrap; active.Margin = new(0, 14, 0, 14); receiving.Children.Add(active);
        refresh.Content = "Làm mới trạng thái"; refresh.Click += async (_, _) => await RunAsync(RefreshAsync); receiving.Children.Add(refresh);
        receiving.Children.Add(Hint("Mật khẩu mở khóa OAuth", new(0, 20, 0, 6))); AddPassword(receiving, unlock);
        download.Content = "Tải, mở khóa & điền cấu hình"; download.Style = (Style)FindResource("Primary"); download.Margin = new(0, 12, 0, 0);
        download.Click += async (_, _) =>
        {
            if (!PasswordFeedback.Required(unlock, "Bạn chưa nhập mật khẩu OAuth.")) { SetStatus("Bạn chưa nhập mật khẩu OAuth.", true); return; }
            await RunAsync(async ct =>
            {
            var secret = unlock.Password; unlock.Clear();
            service ??= createService();
            var revision = await service!.DownloadActiveAsync(ct);
            OAuthConfig parsed;
            try { parsed = await Task.Run(() => OAuthVault.Decrypt(revision, secret), ct); }
            catch (OAuthUnlockException ex) { PasswordFeedback.Invalid(unlock, ex.Message); throw; }
            ct.ThrowIfCancellationRequested();
            if (MessageBox.Show(this, $"Điền bản “{revision.Label}” vào cài đặt máy?\nBạn cần bấm Lưu cài đặt ở cửa sổ trước để áp dụng. Đổi OAuth Client sẽ cần kết nối lại Google.", "Áp dụng cấu hình", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            apply(parsed, revision); SetStatus("Đã mở khóa cấu hình. Đóng cửa sổ này rồi bấm Lưu cài đặt để áp dụng.");
            });
        };
        receiving.Children.Add(download); receiving.Children.Add(Hint("Mật khẩu chỉ mở khóa lần nhận cấu hình này. Sau khi lưu, app dùng bản được Windows mã hóa cho tài khoản trên máy; không hỏi lại mỗi lần chạy. Bản cloud mới không tự ghi đè cấu hình đã lưu.", new(0, 16, 0, 0)));
        tabs.Items.Add(Tab("Lấy OAuth", receiving));
        admin.Margin = new(16);
        admin.Children.Add(Hint("Quản trị cấu hình · chỉ tài khoản được máy chủ cấp quyền admin mới sử dụng được.", new(0, 0, 0, 12)));
        admin.Children.Add(Button("Chọn JSON OAuth Desktop…", async (_, _) => await ReadJsonAsync()));
        preview.TextWrapping = TextWrapping.Wrap; preview.Margin = new(0, 12, 0, 12); admin.Children.Add(preview);
        label.Text = "Cấu hình Google " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"); admin.Children.Add(label);
        admin.Children.Add(Hint("Đặt mật khẩu riêng cho phiên bản (12–256 ký tự)", new(0, 12, 0, 6))); AddPassword(admin, publishPassword);
        admin.Children.Add(Hint("Nhập lại mật khẩu cấu hình", new(0, 10, 0, 6))); AddPassword(admin, confirmPassword);
        admin.Children.Add(Button("Mã hóa, đăng phiên bản & kích hoạt", async (_, _) => await PublishAsync()));
        historyCount.Margin = new(0, 18, 0, 8); historyCount.Text = "LỊCH SỬ CẤU HÌNH"; admin.Children.Add(historyCount); admin.Children.Add(versions);
        more.Content = "Tải thêm lịch sử"; more.Click += async (_, _) => await RunAsync(LoadMoreAsync); admin.Children.Add(more);
        admin.Children.Add(Button("Dùng lại phiên bản đã chọn", async (_, _) => await ActivateAsync()));
        admin.Children.Add(Button("Khóa và đóng quản trị", (_, _) => LockAdministration()));
        adminTab = Tab("Quản trị", admin); adminTab.Visibility = Visibility.Collapsed; tabs.Items.Add(adminTab);
        var footer = new StackPanel(); Grid.SetRow(footer, 2);
        SetStatus("Nhập mật khẩu OAuth để lấy cấu hình. Không cần tài khoản reader.");
        cancel.Content = "Hủy chờ"; cancel.Margin = new(0, 10, 0, 0); cancel.Click += (_, _) => operation?.Cancel(); footer.Children.Add(cancel); root.Children.Add(footer);
        Content = root; UpdateControls();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                if (!busy) OpenAdministration();
            }
        };
        Closing += OnClosing;
        Closed += (_, _) => { lifetime.Cancel(); service?.Dispose(); selectedJson = null; unlock.Clear(); ClearAdminSecrets(); };
        if (autoLoad) Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }
    private static void AddPassword(Panel panel, PasswordBox field) => PasswordFeedback.Add(panel, field);
    private void SetStatus(string message, bool error = false)
    {
        status.Text = (error ? "⚠ " : "● ") + message;
        status.Foreground = error ? Brushes.DarkRed : Brushes.DarkBlue;
        statusCard.Background = new SolidColorBrush(error ? Color.FromRgb(255, 228, 230) : Color.FromRgb(231, 240, 255));
        statusCard.BorderBrush = error ? Brushes.Firebrick : Brushes.CornflowerBlue;
    }
    private static TextBlock Hint(string text, Thickness margin) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = margin };
    private static TabItem Tab(string title, UIElement content) => new() { Header = title, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    private static Button Button(string text, RoutedEventHandler handler) { var button = new Button { Content = text, Margin = new(0, 8, 0, 0) }; button.Click += handler; return button; }
    private void UpdateControls()
    {
        unlock.IsEnabled = !busy; admin.IsEnabled = !busy && service?.IsAdmin == true;
        adminTab.Visibility = adminRequested && service?.IsAdmin == true ? Visibility.Visible : Visibility.Collapsed;
        if (adminTab.Visibility != Visibility.Visible && tabs.SelectedItem == adminTab) tabs.SelectedIndex = 0;
        download.IsEnabled = refresh.IsEnabled = !busy;
        more.IsEnabled = !busy && hasMore;
        cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (busy) return; busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); UpdateControls();
        SetStatus("Đang xử lý yêu cầu Cloud…");
        try { await work(operation.Token); }
        catch (OperationCanceledException) { SetStatus("Đã hủy chờ. Thao tác ghi có thể đã hoàn tất trên Cloud; làm mới trước khi thử lại.", true); }
        catch (HttpRequestException) { SetStatus("Không kết nối được Cloud. Kiểm tra mạng rồi thử lại.", true); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { busy = false; operation.Dispose(); operation = null; UpdateControls(); }
    }
    private void OpenAdministration()
    {
        if (adminRequested) { LockAdministration(); return; }
        var dialog = new AdminSignInWindow(createService) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Session is null) { SetStatus(dialog.Failure.Length > 0 ? dialog.Failure : "Đã hủy mở quản trị.", dialog.Failure.Length > 0); return; }
        service?.Dispose(); service = dialog.Session; adminRequested = true; UpdateControls(); tabs.SelectedItem = adminTab;
        _ = RunAsync(RefreshAsync);
    }
    private void LockAdministration()
    {
        ResetSession(); SetStatus("Đã khóa quản trị. Bạn vẫn có thể nhận OAuth bằng mật khẩu cấu hình.");
    }
    private async Task RefreshAsync(CancellationToken ct)
    {
        service ??= createService();
        current = await service.GetStatusAsync(ct);
        active.Text = current is null ? "Chưa có cấu hình đang dùng. Quản trị viên cần đăng bản đầu tiên." : $"{current.Label}\n{current.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm}\n{(current.PayloadFormat == OAuthVault.Format ? "Được bảo vệ bằng mật khẩu riêng." : "Bản cũ: cần quản trị viên đăng lại có mật khẩu.")}\n{(current.Id == state.Cloud.LastAppliedRevision ? "Máy đã ghi nhận bản này." : "Có thể tải để áp dụng vào máy.")}";
        history.Clear(); historyOffset = 0; hasMore = false; versions.ItemsSource = null;
        if (adminRequested && service.IsAdmin) await LoadMoreAsync(ct);
        SetStatus(current is null ? "Chưa có cấu hình mã hóa đang dùng. Quản trị viên cần đăng bản đầu tiên." : adminRequested ? "Đã xác thực quản trị. Có thể quản lý cấu hình và lịch sử." : "Cloud đã sẵn sàng. Nhập mật khẩu OAuth để tải cấu hình.");
    }
    private async Task LoadMoreAsync(CancellationToken ct)
    {
        var page = await service!.ListVersionsAsync(ct, historyOffset); historyOffset += page.Count;
        foreach (var row in page) if (history.All(existing => existing.Id != row.Id)) history.Add(row);
        hasMore = page.Count == 100; versions.ItemsSource = history.ToList();
        historyCount.Text = $"LỊCH SỬ · {history.Count} bản đã tải{(hasMore ? " · còn trang tiếp theo" : " · đã tải hết")}";
        SetStatus($"Đã tải {history.Count} phiên bản trong lịch sử.");
    }
    private async Task ReadJsonAsync()
    {
        var picker = new OpenFileDialog { Filter = "OAuth Desktop JSON|*.json", Title = "Chọn cấu hình OAuth Desktop" };
        if (picker.ShowDialog(this) != true) return;
        await RunAsync(async ct =>
        {
            if (new FileInfo(picker.FileName).Length > OAuthConfigParser.MaximumBytes) throw new InvalidOperationException("Tệp JSON vượt 64 KB.");
            var json = await File.ReadAllTextAsync(picker.FileName, ct); var parsed = OAuthConfigParser.Parse(json);
            selectedJson = json; preview.Text = "OAuth Desktop hợp lệ\nClient ID: " + parsed.ClientId;
            SetStatus("Đã kiểm tra tệp. Đặt mật khẩu riêng để mã hóa trước khi đăng.");
        });
    }
    private async Task PublishAsync()
    {
        if (selectedJson is null) { SetStatus("Chọn JSON trước.", true); return; }
        var secret = publishPassword.Password;
        try { OAuthVault.ValidateNewPassword(secret); }
        catch (InvalidOperationException ex) { PasswordFeedback.Invalid(publishPassword, ex.Message); SetStatus(ex.Message, true); return; }
        if (secret != confirmPassword.Password) { PasswordFeedback.Invalid(confirmPassword, "Hai mật khẩu cấu hình chưa khớp."); SetStatus("Hai mật khẩu cấu hình chưa khớp.", true); return; }
        if (MessageBox.Show(this, $"Mã hóa và đăng “{label.Text}” làm bản đang dùng? Hãy lưu mật khẩu riêng ở nơi an toàn và cung cấp cho máy nhận. Cloud không lưu mật khẩu và không khôi phục được nếu quên.", "Đăng cấu hình", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        publishPassword.Clear(); confirmPassword.Clear();
        await RunAsync(async ct =>
        {
            var json = selectedJson!; var clientId = OAuthConfigParser.Parse(json).ClientId;
            var envelope = await Task.Run(() => OAuthVault.Encrypt(json, secret), ct);
            var revision = await service!.PublishLockedAsync(label.Text, clientId, envelope, current?.Id, ct);
            selectedJson = null; preview.Text = "";
            await RefreshAsync(ct);
            SetStatus("Đã đăng bản mã hóa: " + revision.Label);
        });
    }
    private async Task ActivateAsync()
    {
        if (versions.SelectedItem is not CloudRevision selected) { SetStatus("Chọn bản trong lịch sử.", true); return; }
        if (selected.PayloadFormat != OAuthVault.Format) { SetStatus("Bản cũ chưa mã hóa. Chọn JSON gốc và đăng lại cùng mật khẩu riêng.", true); return; }
        if (MessageBox.Show(this, "Dùng lại “" + selected.Label + "”? Máy nhận phải biết mật khẩu của đúng phiên bản đó.", "Chọn cấu hình", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await RunAsync(async ct => { await service!.ActivateAsync(selected.Id, current?.Id, ct); await RefreshAsync(ct); SetStatus("Đã chọn lại: " + selected.Label); });
    }
    private void ClearAdminSecrets() { publishPassword.Clear(); confirmPassword.Clear(); selectedJson = null; preview.Text = ""; }
    private void ResetSession()
    {
        service?.Dispose(); service = null; current = null; adminRequested = false; ClearAdminSecrets(); unlock.Clear();
        history.Clear(); historyOffset = 0; hasMore = false; versions.ItemsSource = null;
        active.Text = "Bấm Làm mới để kiểm tra cấu hình."; UpdateControls();
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; SetStatus("Đang xử lý. Bấm Hủy chờ trước khi đóng.", true); } }
}
