using System.Net.Mail;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using DriveLink.Services;
namespace DriveLink;

public class PromptWindow : Window
{
    private readonly TextBox input = new() { Margin = new(0, 16, 0, 16) };
    public string Value => input.Text.Trim();
    public PromptWindow(string title, string description, string hint, string buttonText = "Tạo không gian", string initial = "", int maxLength = 100)
    {
        Title = title; Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new(24) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) });
        input.ToolTip = hint; input.Text = initial; stack.Children.Add(input);
        var button = new Button { Content = buttonText, IsDefault = true, Style = (Style)FindResource("Primary") };
        button.Click += (_, _) => { if (Value.Length > 0 && Value.Length <= maxLength) DialogResult = true; else MessageBox.Show(this, $"Tên cần từ 1 đến {maxLength} ký tự."); };
        stack.Children.Add(button); Content = stack; Loaded += (_, _) => input.Focus();
    }
    public static string? Ask(Window owner, string title, string description, string hint)
    {
        var dialog = new PromptWindow(title, description, hint) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
public class SettingsWindow : Window
{
    public SettingsWindow(SavedState state, SecureStore store, Action<Window>? openUpdates = null)
    {
        Title = "Cài đặt — DriveLink · Quang Huy"; Width = 680; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var installed = CompressionTools.Detect();
        var automaticScale = new CheckBox { Content = "Tự chọn tỷ lệ theo màn hình", IsChecked = state.Settings.UiScale == 0, Margin = new(0, 12, 0, 8) };
        var uiScale = new Slider { Minimum = 60, Maximum = 150, TickFrequency = 5, IsSnapToTickEnabled = true, Value = DisplayPrivacy.ResolveScale(state.Settings.UiScale, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height) * 100, IsEnabled = state.Settings.UiScale != 0 };
        var scaleLabel = new TextBlock { Text = $"Thu phóng giao diện: {uiScale.Value:0}%", Margin = new(0, 8, 0, 8) };
        automaticScale.Checked += (_, _) => uiScale.IsEnabled = false; automaticScale.Unchecked += (_, _) => uiScale.IsEnabled = true;
        uiScale.ValueChanged += (_, _) => scaleLabel.Text = $"Thu phóng giao diện: {uiScale.Value:0}%";
        var autoUpdates = new CheckBox { Content = "Tự kiểm tra phiên bản mới khi mở app và mỗi 6 giờ", IsChecked = state.Settings.CheckUpdatesOnStartup, Margin = new(0, 18, 0, 16) };
        var showOverlay = new CheckBox { Content = "Hiển thị lớp thông báo khi kéo file vào app", IsChecked = state.Settings.ShowDropOverlay, Margin = new(0, 12, 0, 12) };
        var opacity = new Slider { Minimum = 0, Maximum = 100, Value = Math.Clamp(state.Settings.DropOverlayOpacity * 100, 0, 100), TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new(0, 8, 0, 8) };
        var opacityLabel = new TextBlock { Text = $"Độ đậm nền: {opacity.Value:0}% (100% = không trong suốt)", Foreground = Brushes.SlateGray };
        opacity.ValueChanged += (_, _) => opacityLabel.Text = $"Độ đậm nền: {opacity.Value:0}% (100% = không trong suốt)";
        var background = new CheckBox { Content = "Thu nhỏ/đóng cửa sổ vào khay hệ thống và chạy nền", IsChecked = state.Settings.RunInBackground, Margin = new(0, 24, 0, 12) };
        var startup = new CheckBox { Content = "Chạy khi đăng nhập Windows", IsChecked = StartupRegistration.IsEnabled(), Margin = new(0, 12, 0, 12) };
        var format = new ComboBox { ItemsSource = new[] { "ZIP — mặc định, dễ mở trên máy nhận", "RAR — cần WinRAR để tạo" }, SelectedIndex = state.Settings.ArchiveFormat == "rar" ? 1 : 0, Margin = new(0, 10, 0, 20) };
        var toolKeys = new[] { "auto", "7zip", "winrar", "builtin" };
        var tool = new ComboBox { ItemsSource = new[] { "Tự động: 7-Zip → WinRAR → ZIP tích hợp", "7-Zip (ZIP)", "WinRAR (ZIP/RAR)", "ZIP tích hợp (không cần công cụ ngoài)" }, SelectedIndex = Math.Max(0, Array.IndexOf(toolKeys, state.Settings.CompressionTool)), Margin = new(0, 10, 0, 20) };
        var strong = new CheckBox { Content = "Ưu tiên dung lượng nhỏ hơn (nén mạnh, mất thời gian hơn)", IsChecked = state.Settings.HighCompression, Margin = new(0, 0, 0, 16) };
        var stack = new StackPanel { Margin = new(26) };
        stack.Children.Add(new TextBlock { Text = "Kết nối Google của bạn", FontSize = 24, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = "Chỉ nhập JSON một lần trên máy này cho cùng OAuth Client. Khi thêm tài khoản Google khác, bấm + Tài khoản, không cần tải JSON mới. Nếu đang ở Testing, thêm email mới vào Test users trên Google Cloud. Máy mới chưa lưu cấu hình thì nhập lại cùng JSON.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 18) });
        var import = new Button { Content = "Nhập tệp OAuth Desktop từ Google…" };
        var clientId = new TextBox { Text = state.Config.ClientId, Margin = new(0, 8, 0, 12), IsReadOnly = true };
        var config = state.Config;
        string? pendingCloudRevision = null;
        import.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Google OAuth JSON|*.json", Title = "Chọn tệp OAuth loại Desktop app" };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                if (new FileInfo(picker.FileName).Length > OAuthConfigParser.MaximumBytes) throw new InvalidOperationException("Tệp JSON vượt 64 KB.");
                var next = OAuthConfigParser.Parse(File.ReadAllText(picker.FileName));
                config = next; clientId.Text = next.ClientId; pendingCloudRevision = null;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Không đọc được cấu hình"); }
        };
        stack.Children.Add(import); stack.Children.Add(clientId);
        var cloud = new Button { Content = "Cloud lấy OAuth…", Margin = new(0, 0, 0, 12) };
        cloud.Click += (_, _) => new CloudConfigWindow(state, store, (next, revision) => { config = next; clientId.Text = next.ClientId; pendingCloudRevision = revision.Id; }) { Owner = this }.ShowDialog();
        stack.Children.Add(cloud);
        stack.Children.Add(new TextBlock { Text = "Cloud được thiết lập sẵn. Chỉ cần mật khẩu OAuth để nhận cấu hình trên máy mới; không cần nhập tài khoản reader.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 0, 0, 12) });
        stack.Children.Add(new TextBlock { Text = "Thiết lập: bật Google Drive API → cấu hình Google Auth Platform → thêm Test users → tạo OAuth Client loại Desktop app → tải JSON. Xem README.md để biết chi tiết.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 0, 0, 12) });
        stack.Children.Add(new TextBlock { Text = "Bản đầu yêu cầu quyền Drive đầy đủ để tìm thư mục được chia sẻ giữa các tài khoản. OAuth Testing có thể yêu cầu kết nối lại sau 7 ngày. Chưa cấu hình Google? Bạn vẫn có thể xem thử giao diện.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 0, 0, 20) });
        var save = new Button { Content = "Lưu cấu hình", Style = (Style)FindResource("Primary") };
        save.Click += (_, _) =>
        {
            try
            {
                var nextSettings = new UserSettings { UiScale = automaticScale.IsChecked == true ? 0 : uiScale.Value / 100, CheckUpdatesOnStartup = autoUpdates.IsChecked == true, ShowDropOverlay = showOverlay.IsChecked == true, DropOverlayOpacity = opacity.Value / 100, RunInBackground = background.IsChecked == true, StartWithWindows = startup.IsChecked == true, ArchiveFormat = format.SelectedIndex == 1 ? "rar" : "zip", CompressionTool = toolKeys[tool.SelectedIndex], HighCompression = strong.IsChecked == true };
                if (nextSettings.ArchiveFormat == "rar" && (!installed.CanCreateRar || nextSettings.CompressionTool is "7zip" or "builtin")) throw new InvalidOperationException("RAR cần WinRAR đã cài và công cụ Tự động hoặc WinRAR. Chọn ZIP nếu muốn dùng công cụ khác.");
                var previousConfig = state.Config; var previousSettings = state.Settings; var previousAccounts = state.Accounts.ToList(); var previousSelected = state.SelectedAccountId;
                var previousRevision = state.Cloud.LastAppliedRevision;
                if (config.ClientId != state.Config.ClientId && state.Accounts.Count > 0)
                {
                    if (MessageBox.Show(this, "Đổi OAuth Client cần kết nối lại các tài khoản trên máy này. Tiếp tục?", "Đổi cấu hình", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    state.Accounts.Clear(); state.SelectedAccountId = null;
                }
                try
                {
                    state.Cloud.LastAppliedRevision = pendingCloudRevision ?? (config == previousConfig ? previousRevision : "");
                    state.Config = config; state.Settings = nextSettings; store.Save(state);
                    if (nextSettings.StartWithWindows || StartupRegistration.IsEnabled()) StartupRegistration.SetEnabled(nextSettings.StartWithWindows);
                }
                catch
                {
                    state.Cloud.LastAppliedRevision = previousRevision;
                    state.Config = previousConfig; state.Settings = previousSettings; state.Accounts = previousAccounts; state.SelectedAccountId = previousSelected; store.Save(state); throw;
                }
                DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Chưa lưu được"); }
        };
        save.Content = "Lưu cài đặt";
        stack.Children.Add(new TextBlock { Text = "TÀI KHOẢN ĐÃ LIÊN KẾT", FontSize = 12, Margin = new(0, 24, 0, 10) });
        var accounts = new ListBox { ItemsSource = state.Accounts.ToList(), Height = 100 };
        stack.Children.Add(accounts);
        var remove = new Button { Content = "Xóa tài khoản khỏi máy này", Margin = new(0, 10, 0, 0) };
        remove.Click += (_, _) =>
        {
            if (accounts.SelectedItem is not Account account) return;
            if (MessageBox.Show(this, "Xóa kết nối đã lưu cho " + account.Email + "? File trên Drive không bị xóa. Quyền Google của ứng dụng có thể thu hồi riêng tại myaccount.google.com/connections.", "Xóa khỏi máy", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { state.Accounts.Remove(account); store.Save(state); accounts.ItemsSource = state.Accounts.ToList(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Chưa lưu được"); }
        };
        stack.Children.Add(remove);
        var general = new StackPanel { Margin = new(24) };
        general.Children.Add(new TextBlock { Text = "Giao diện và chạy nền", FontSize = 24, FontWeight = FontWeights.SemiBold });
        general.Children.Add(automaticScale); general.Children.Add(scaleLabel); general.Children.Add(uiScale);
        general.Children.Add(new TextBlock { Text = "Ctrl + cuộn chuột hoặc Ctrl + / − để thu phóng; Ctrl + 0 về tự động. Nút thu phóng nằm dưới thanh bên.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 8, 0, 12) });
        general.Children.Add(showOverlay); general.Children.Add(opacityLabel); general.Children.Add(opacity);
        general.Children.Add(new TextBlock { Text = "Điều chỉnh lớp phủ khi kéo thả; file vẫn gửi được khi tắt lớp thông báo.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray });
        general.Children.Add(background); general.Children.Add(new TextBlock { Text = "Bật: nút thu nhỏ hoặc X sẽ ẩn app vào khay hệ thống cạnh đồng hồ; truyền file và cập nhật tiếp tục. Nhấp đúp icon để mở lại; chuột phải → Thoát hoàn toàn để tắt app.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray });
        general.Children.Add(startup); general.Children.Add(new TextBlock { Text = "Đăng ký cho người dùng Windows hiện tại. Nếu bật cả chạy nền, app khởi động trong khay hệ thống. Windows có thể trì hoãn tác vụ khởi động.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray });
        var restore = new Button { Content = "Hiện lại các không gian đã ẩn trên máy", Margin = new(0, 24, 0, 12) };
        restore.Click += (_, _) => { try { foreach (var preference in state.SpacePreferences.Values) preference.Hidden = false; store.Save(state); restore.Content = "Đã hiện lại — đóng cài đặt để làm mới"; } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Chưa lưu được"); } };
        general.Children.Add(restore);
        general.Children.Add(new TextBlock { Text = "Màu và ghim được lưu riêng trên máy này. Chuột phải lên không gian để đánh dấu, ghim, ẩn hoặc đưa vào Thùng rác Drive.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray });
        var compression = new StackPanel { Margin = new(24) };
        compression.Children.Add(new TextBlock { Text = "Gửi cả thư mục", FontSize = 24, FontWeight = FontWeights.SemiBold });
        compression.Children.Add(new TextBlock { Text = "Thả thư mục vào app hoặc bấm Chọn thư mục. Ứng dụng nén trước, rồi gửi một file đóng gói; thư mục gốc được giữ nguyên.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 16, 0, 20) });
        compression.Children.Add(new TextBlock { Text = "Định dạng" }); compression.Children.Add(format);
        compression.Children.Add(new TextBlock { Text = "Công cụ nén" }); compression.Children.Add(tool); compression.Children.Add(strong);
        compression.Children.Add(new TextBlock { Text = installed.Summary, Foreground = Brushes.SlateGray, Margin = new(0, 0, 0, 16) });
        compression.Children.Add(new TextBlock { Text = "ZIP dễ trao đổi; RAR có thể nén tốt hơn, nhất là nhiều file tương tự. Ảnh/video/file đã nén thường giảm ít. Nén mạnh cần thêm thời gian và RAM. ZIP luôn có phương án tích hợp nếu chưa cài công cụ ngoài. Không tự cài hoặc tải phần mềm nén.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray });
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Giao diện & hệ thống", Content = new ScrollViewer { Content = general, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "Nén thư mục", Content = new ScrollViewer { Content = compression, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "Tài khoản Google", Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var updates = new StackPanel { Margin = new(24) };
        updates.Children.Add(new TextBlock { Text = "Cập nhật phần mềm", FontSize = 24, FontWeight = FontWeights.SemiBold });
        updates.Children.Add(new TextBlock { Text = "Đang dùng DriveLink " + AppBrand.Version, Margin = new(0, 16, 0, 0) });
        updates.Children.Add(autoUpdates);
        var checkUpdates = new Button { Content = "Kiểm tra và cập nhật…", IsEnabled = openUpdates is not null };
        checkUpdates.Click += (_, _) => openUpdates?.Invoke(this); updates.Children.Add(checkUpdates);
        updates.Children.Add(new TextBlock { Text = "App thông báo khi có bản mới và chỉ tải/cài khi bạn chọn cập nhật. Trước khi khởi động lại, app chờ các lượt gửi/nhận hoàn tất. Tài khoản và các cài đặt đã lưu được giữ nguyên. Tắt kiểm tra tự động vẫn có thể kiểm tra thủ công.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 18, 0, 0) });
        tabs.Items.Add(new TabItem { Header = "Cập nhật", Content = new ScrollViewer { Content = updates, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var content = new Grid { Margin = new(12) }; content.RowDefinitions.Add(new RowDefinition()); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(tabs); Grid.SetRow(save, 1); save.Margin = new(12, 12, 12, 4); content.Children.Add(save); Content = content;
    }
}
public class MembersWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    public MembersWindow(DriveService drive, Account account, Space space)
    {
        Title = "Thành viên — " + space; Width = 600; Height = 590; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new(24) };
        stack.Children.Add(new TextBlock { Text = "Chia sẻ không gian", FontSize = 24, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = "Mời tài khoản Google trên máy còn lại. Người nhận mở cùng ứng dụng và bấm làm mới để thấy không gian.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 16) });
        var email = new TextBox { ToolTip = "Địa chỉ email người nhận", Margin = new(0, 0, 0, 10) };
        var roles = new ComboBox { ItemsSource = new[] { "Chỉ xem và tải về", "Gửi, chỉnh sửa và tải về" }, SelectedIndex = 0, Margin = new(0, 0, 0, 10) };
        var invite = new Button { Content = "Mời bằng email", Style = (Style)FindResource("Primary") };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12), Foreground = Brushes.SlateGray };
        var list = new ListBox { Height = 170 };
        var remove = new Button { Content = "Thu hồi quyền thành viên đã chọn", Margin = new(0, 12, 0, 0) };
        stack.Children.Add(email); stack.Children.Add(roles); stack.Children.Add(invite); stack.Children.Add(status); stack.Children.Add(list); stack.Children.Add(remove);
        stack.Children.Add(new TextBlock { Text = "Thao tác áp dụng cho thư mục chung. Google quyết định quyền bạn có thể thay đổi. Quyền riêng được cấp trực tiếp trên từng file cần quản lý trên Drive; thu hồi không xóa bản đã tải về.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 12, Margin = new(0, 12, 0, 0) });
        async Task Run(Func<Task> work)
        {
            if (busy) return;
            busy = true; invite.IsEnabled = false; remove.IsEnabled = false;
            try { await work(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; invite.IsEnabled = true; remove.IsEnabled = true; }
        }
        async Task LoadMembers() { list.ItemsSource = await drive.ListPermissionsAsync(account, space.Id, lifetime.Token); }
        Loaded += async (_, _) => await Run(LoadMembers);
        invite.Click += async (_, _) =>
        {
            if (!MailAddress.TryCreate(email.Text.Trim(), out var parsed) || parsed.Address != email.Text.Trim()) { status.Text = "Nhập địa chỉ email hợp lệ."; return; }
            await Run(async () => { await drive.InviteAsync(account, space.Id, parsed.Address, roles.SelectedIndex == 1 ? "writer" : "reader", lifetime.Token); status.Text = "Đã cấp quyền và yêu cầu Google gửi email thông báo."; await LoadMembers(); });
        };
        remove.Click += async (_, _) =>
        {
            if (list.SelectedItem is not DrivePermission permission) return;
            if (permission.Role == "owner") { status.Text = "Không thể thu hồi quyền chủ sở hữu."; return; }
            if (permission.Email == account.Email) { status.Text = "Bản đầu không hỗ trợ tự thu hồi quyền của tài khoản đang dùng."; return; }
            if (MessageBox.Show(this, "Thu hồi quyền truy cập thư mục của " + permission + "?", "Thu hồi quyền", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await Run(async () => { await drive.RemovePermissionAsync(account, space.Id, permission.Id, lifetime.Token); status.Text = "Đã thu hồi quyền trên thư mục."; await LoadMembers(); });
        };
        Closed += (_, _) => lifetime.Cancel();
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

