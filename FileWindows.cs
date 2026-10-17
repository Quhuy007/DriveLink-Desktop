using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DriveLink.Services;
namespace DriveLink;

public sealed class FileShareWindow : Window
{
    private readonly DriveService drive;
    private readonly Account account;
    private DriveItem file;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy, applied, loaded;
    private List<DrivePermission> permissions = [];
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) };
    private readonly StackPanel people = new();
    private readonly TextBox email = new() { ToolTip = "Email người nhận hoặc nhóm Google", Margin = new(0, 6, 0, 8) };
    private readonly TextBox message = new() { ToolTip = "Lời nhắn gửi kèm email", AcceptsReturn = true, Height = 54, Margin = new(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox recipientRole = Roles(), generalRole = Roles();
    private readonly ComboBox recipientType = new() { ItemsSource = new[] { "Người nhận", "Nhóm Google" }, SelectedIndex = 0 };
    private readonly ComboBox general = new() { ItemsSource = new[] { "Bị hạn chế", "Bất kỳ ai có đường liên kết", "Tên miền Google Workspace" }, SelectedIndex = 0, Margin = new(0, 6, 0, 8) };
    private readonly TextBox domain = new() { ToolTip = "Ví dụ: congty.com", Margin = new(0, 0, 0, 8) };
    private readonly CheckBox notify = new() { Content = "Thông báo cho người nhận", IsChecked = true, Margin = new(0, 10, 0, 0) };
    private readonly Button send = new() { Content = "Gửi lời mời", Margin = new(0, 8, 0, 0) }, saveGeneral = new() { Content = "Lưu quyền truy cập chung", Margin = new(0, 8, 0, 0) };
    private readonly CheckBox writersShare = new() { Content = "Người chỉnh sửa có thể thay đổi quyền và chia sẻ", Margin = new(0, 6, 0, 6) }, restrictCopy = new() { Content = "Chặn tải xuống, in và sao chép đối với người xem / nhận xét", Margin = new(0, 6, 0, 6) };
    private readonly Button saveOptions = new() { Content = "Lưu tùy chọn chia sẻ", Margin = new(0, 6, 0, 0) };
    private static readonly string[] RoleKeys = ["reader", "commenter", "writer"];
    private static ComboBox Roles() => new() { ItemsSource = new[] { "Người xem", "Người nhận xét", "Người chỉnh sửa" }, SelectedIndex = 0, MinWidth = 138 };
    public FileShareWindow(DriveService drive, Account account, DriveItem file)
    {
        this.drive = drive; this.account = account; this.file = file;
        Title = "Chia sẻ — " + file.Name; Width = 620; Height = Math.Min(750, SystemParameters.WorkArea.Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "Chia sẻ “" + file.Name + "”", TextWrapping = TextWrapping.Wrap, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock { Text = "Thêm người hoặc nhóm", FontWeight = FontWeights.SemiBold }); panel.Children.Add(email);
        var inviteOptions = new Grid(); inviteOptions.ColumnDefinitions.Add(new()); inviteOptions.ColumnDefinitions.Add(new());
        recipientType.Margin = new(0, 0, 8, 0); inviteOptions.Children.Add(recipientType); Grid.SetColumn(recipientRole, 1); inviteOptions.Children.Add(recipientRole); panel.Children.Add(inviteOptions);
        panel.Children.Add(notify); panel.Children.Add(new TextBlock { Text = "Tin nhắn (không bắt buộc)", Foreground = Brushes.SlateGray, Margin = new(0, 8, 0, 0) }); panel.Children.Add(message); panel.Children.Add(send);
        send.Style = (Style)FindResource("Primary");
        panel.Children.Add(new TextBlock { Text = "Những người có quyền truy cập", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(0, 22, 0, 8) });
        panel.Children.Add(people);
        panel.Children.Add(new TextBlock { Text = "Quyền truy cập chung", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(0, 20, 0, 4) });
        panel.Children.Add(general); panel.Children.Add(domain); panel.Children.Add(generalRole); panel.Children.Add(saveGeneral);
        var note = new TextBlock { Text = "Bị hạn chế: chỉ người đã được cấp quyền có thể mở. Quyền kế thừa từ không gian/thư mục vẫn có hiệu lực. Quyền và giới hạn của tổ chức được Google kiểm tra.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 12, Margin = new(0, 10, 0, 12) }; panel.Children.Add(note);
        var options = new StackPanel(); options.Children.Add(writersShare); options.Children.Add(restrictCopy); options.Children.Add(saveOptions);
        panel.Children.Add(new Expander { Header = "Tùy chọn chia sẻ", Content = options, Margin = new(0, 4, 0, 16) });
        saveOptions.Click += async (_, _) => await Run(async ct => { await drive.SetSharingOptionsAsync(account, file.Id, file.Owners.Any(x => x.Me) ? writersShare.IsChecked == true : null, file.Capabilities.CanChangeCopyRequiresWriterPermission ? restrictCopy.IsChecked == true : null, ct); applied = true; await Load(ct); SetStatus("Đã lưu tùy chọn chia sẻ."); });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = "Sao chép liên kết", Margin = new(0, 0, 8, 0) }; copy.Click += (_, _) => { try { Clipboard.SetText(file.BrowserUrl); SetStatus("Đã sao chép liên kết. Quyền truy cập được giữ nguyên."); } catch { SetStatus("Chưa sao chép được liên kết.", true); } }; footer.Children.Add(copy);
        var done = new Button { Content = "Xong", Style = (Style)FindResource("Primary") }; done.Click += (_, _) => Close(); footer.Children.Add(done); panel.Children.Add(footer);
        var advanced = new Button { Content = "Các tùy chọn khác trên Google Drive", Margin = new(0, 10, 0, 0) }; advanced.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(file.BrowserUrl) { UseShellExecute = true }); } catch { SetStatus("Chưa mở được trình duyệt.", true); } }; panel.Children.Add(advanced);
        var refresh = new Button { Content = "Làm mới quyền truy cập", Margin = new(0, 10, 0, 0) }; refresh.Click += async (_, _) => await Run(Load); panel.Children.Add(refresh);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        general.SelectionChanged += (_, _) => ShowGeneralOptions(); notify.Checked += (_, _) => message.IsEnabled = true; notify.Unchecked += (_, _) => message.IsEnabled = false;
        send.Click += async (_, _) =>
        {
            var value = email.Text.Trim();
            if (!System.Net.Mail.MailAddress.TryCreate(value, out var parsed) || parsed.Address != value) { email.BorderBrush = Brushes.Firebrick; SetStatus("Nhập email hợp lệ của người nhận hoặc nhóm.", true); email.Focus(); return; }
            email.ClearValue(TextBox.BorderBrushProperty);
            await Run(async ct => { await drive.GrantPermissionAsync(account, file.Id, recipientType.SelectedIndex == 1 ? "group" : "user", value, RoleKeys[recipientRole.SelectedIndex], notify.IsChecked == true, message.Text, ct); applied = true; await Load(ct); email.Clear(); SetStatus("Đã cấp quyền cho " + DisplayPrivacy.MaskEmail(value) + "."); });
        };
        saveGeneral.Click += async (_, _) =>
        {
            var type = general.SelectedIndex == 1 ? "anyone" : "domain";
            var desired = general.SelectedIndex; var role = RoleKeys[generalRole.SelectedIndex]; var recipientDomain = domain.Text.Trim().ToLowerInvariant();
            if (desired == 2 && (Uri.CheckHostName(recipientDomain) != UriHostNameType.Dns || !recipientDomain.Contains('.'))) { domain.BorderBrush = Brushes.Firebrick; SetStatus("Nhập tên miền Google Workspace hợp lệ.", true); return; }
            domain.ClearValue(TextBox.BorderBrushProperty);
            if (desired != 0 && MessageBox.Show(this, desired == 1 ? "Cho phép bất kỳ ai có liên kết truy cập file với quyền đã chọn?" : "Cho phép người dùng trong tên miền này truy cập bằng liên kết?", "Đổi quyền truy cập chung", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await Run(async ct =>
            {
                var broad = permissions.Where(p => p.Type is "anyone" or "domain").ToList();
                if (broad.Any(p => p.Inherited)) throw new InvalidOperationException("Quyền chung được kế thừa. Hãy thay đổi trên thư mục hoặc Drive gốc.");
                // Narrow/remove old broad grants first. Failed writes never trigger a blind retry.
                var matching = desired == 0 ? null : broad.FirstOrDefault(p => p.Type == type && (type != "domain" || p.Domain == recipientDomain));
                foreach (var permission in broad.Where(p => p != matching)) { await drive.RemovePermissionAsync(account, file.Id, permission.Id, ct); applied = true; }
                if (desired != 0)
                {
                    if (matching is not null) await drive.ChangePermissionAsync(account, file.Id, matching.Id, role, ct, linkOnly: true);
                    else await drive.GrantPermissionAsync(account, file.Id, type, recipientDomain, role, false, "", ct);
                    applied = true;
                }
                await Load(ct); SetStatus("Đã lưu quyền truy cập chung.");
            });
        };
        Loaded += async (_, _) => await Run(Load);
        Closing += (_, e) => { if (busy) { e.Cancel = true; SetStatus("Đang xử lý với Google. Chờ kết quả trước khi đóng cửa sổ."); } };
        Closed += (_, _) => lifetime.Cancel(); ShowGeneralOptions(); SetEnabled(false);
    }
    private void SetStatus(string text, bool error = false) { status.Text = (error ? "⚠ " : "") + text; status.Foreground = error ? Brushes.Firebrick : Brushes.SlateGray; }
    private void SetEnabled(bool enabled)
    {
        send.IsEnabled = saveGeneral.IsEnabled = email.IsEnabled = recipientType.IsEnabled = recipientRole.IsEnabled = general.IsEnabled = generalRole.IsEnabled = domain.IsEnabled = notify.IsEnabled = enabled;
        people.IsEnabled = !busy && loaded; message.IsEnabled = enabled && notify.IsChecked == true;
        writersShare.IsEnabled = enabled && file.Owners.Any(x => x.Me);
        restrictCopy.IsEnabled = enabled && file.Capabilities.CanChangeCopyRequiresWriterPermission;
        saveOptions.IsEnabled = writersShare.IsEnabled || restrictCopy.IsEnabled;
    }
    private void ShowGeneralOptions() { domain.Visibility = general.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; generalRole.Visibility = general.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible; }
    private async Task Run(Func<CancellationToken, Task> work)
    {
        if (busy) return; busy = true; applied = false; SetEnabled(false); SetStatus("Đang tải / lưu quyền truy cập…");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await work(timeout.Token); }
        catch (Exception ex) { loaded = false; SetStatus((applied ? "Một số thay đổi đã được lưu. Bấm làm mới hoặc mở lại để kiểm tra trước khi thử tiếp. " : "Thao tác chưa hoàn tất. ") + (ex is OperationCanceledException ? "Yêu cầu quá thời gian; kiểm tra quyền trên Drive trước khi gửi lại." : ex.Message), true); }
        finally { busy = false; SetEnabled(loaded && file.Capabilities.CanShare); }
    }
    private async Task Load(CancellationToken ct)
    {
        loaded = false;
        file = await drive.GetFileAsync(account, file.Id, ct);
        permissions = await drive.ListPermissionsAsync(account, file.Id, ct);
        loaded = true;
        writersShare.IsChecked = file.WritersCanShare; restrictCopy.IsChecked = file.CopyRequiresWriterPermission;
        people.Children.Clear();
        foreach (var permission in permissions.Where(p => p.Type is "user" or "group"))
        {
            var row = new Grid { Margin = new(0, 4, 0, 4) }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var name = new TextBlock { Text = (permission.Name.Length > 0 ? permission.Name + "\n" : "") + DisplayPrivacy.MaskEmail(permission.Email) + (permission.Inherited ? " · Kế thừa" : ""), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 8, 0) }; row.Children.Add(name);
            if (permission.Role == "owner") { var owner = new TextBlock { Text = "Chủ sở hữu", VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(owner, 1); row.Children.Add(owner); }
            else
            {
                var role = new ComboBox { ItemsSource = new[] { "Người xem", "Người nhận xét", "Người chỉnh sửa", "Xóa quyền truy cập" }, SelectedIndex = Array.IndexOf(RoleKeys, permission.Role), MinWidth = 150, IsEnabled = file.Capabilities.CanShare && RoleKeys.Contains(permission.Role) && !permission.Inherited && !permission.Email.Equals(account.Email, StringComparison.OrdinalIgnoreCase) };
                role.SelectionChanged += async (_, _) =>
                {
                    var index = role.SelectedIndex; if (busy || index < 0 || index < 3 && RoleKeys[index] == permission.Role) return;
                    if (index == 3 && MessageBox.Show(this, "Thu hồi quyền của " + DisplayPrivacy.MaskEmail(permission.Email) + "?", "Thu hồi quyền", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { role.SelectedIndex = Array.IndexOf(RoleKeys, permission.Role); return; }
                    await Run(async token => { if (index == 3) await drive.RemovePermissionAsync(account, file.Id, permission.Id, token); else await drive.ChangePermissionAsync(account, file.Id, permission.Id, RoleKeys[index], token); applied = true; await Load(token); SetStatus("Đã cập nhật quyền người nhận."); });
                };
                Grid.SetColumn(role, 1); row.Children.Add(role);
            }
            people.Children.Add(row);
        }
        var broad = permissions.FirstOrDefault(p => p.Type == "anyone") ?? permissions.FirstOrDefault(p => p.Type == "domain");
        general.SelectedIndex = broad is null ? 0 : broad.Type == "anyone" ? 1 : 2;
        generalRole.SelectedIndex = Math.Max(0, Array.IndexOf(RoleKeys, broad?.Role ?? "reader")); domain.Text = broad?.Domain ?? "";
        SetStatus(file.Capabilities.CanShare ? "Quản lý người nhận và liên kết của file." : "Tài khoản này chỉ được xem quyền truy cập; không có quyền thay đổi chia sẻ.");
    }
}

public sealed class FileDetailsWindow : Window
{
    public FileDetailsWindow(DriveItem file)
    {
        Title = "Thông tin chi tiết — " + file.Name; Width = 520; Height = Math.Min(650, SystemParameters.WorkArea.Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "Thông tin chi tiết", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 20) });
        void Add(string label, string value) { panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.SlateGray, Margin = new(0, 10, 0, 4) }); panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold }); }
        Add("Tên file", file.Name); Add("Loại", file.Kind + " · " + file.MimeType); Add("Dung lượng", file.SizeLabel); Add("Chủ sở hữu", file.OwnerLabel);
        Add("Được tạo", file.TimeLabel); Add("Được sửa", file.ModifiedAt == default ? "Chưa có thông tin" : file.ModifiedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"));
        Add("Trạng thái", file.IsLocked ? "Đã khóa chỉnh sửa" : "Chưa khóa");
        if (file.Description.Length > 0) Add("Mô tả", file.Description);
        foreach (var restriction in file.ContentRestrictions.Where(x => x.ReadOnly && x.Reason.Length > 0)) Add("Lý do khóa", restriction.Reason);
        var open = new Button { Content = "Mở trên Google Drive", Margin = new(0, 20, 0, 0) }; open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(file.BrowserUrl) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } }; panel.Children.Add(open);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
