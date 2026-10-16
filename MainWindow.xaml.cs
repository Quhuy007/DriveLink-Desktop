using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using DriveLink.Services;

namespace DriveLink;
public partial class MainWindow : Window
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SecureStore store;
    private readonly SavedState state;
    private readonly GoogleAuth auth;
    private readonly DriveService drive;
    private readonly LocalFileHistory history;
    private readonly FolderCompressor compressor = new();
    private readonly TrayController tray;
    private bool exitRequested, folderOpening;
    private readonly TransferLane uploads = new(), downloads = new();
    private readonly HashSet<string> downloadTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer speedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset lastUploadProgress, lastDownloadProgress;
    private long uploadVersion, downloadVersion;
    private bool HasTransfers => uploads.Count > 0 || downloads.Count > 0;
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private List<DriveItem> files = [];
    private bool busy, refreshing, applying, demo;
    private int filter;
    private DateTimeOffset lastSpaceRefresh;
    private Account? CurrentAccount => AccountsBox.SelectedItem as Account;
    private Space? CurrentSpace => SpacesList.SelectedItem as Space;
    private DriveItem? CurrentFile => FilesGrid.SelectedItem as DriveItem;

    public MainWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width); MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width); Height = Math.Min(Height, workArea.Height);
        FilesGrid.PreviewMouseRightButtonDown += FileRow_RightClick;
        store = new SecureStore();
        try { state = store.Load(); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or IOException)
        {
            state = new();
            MessageBox.Show("Không đọc được tài khoản đã lưu. Dữ liệu hiện có chưa bị ghi đè; hãy sao lưu accounts.dat trước khi kết nối lại.\n" + ex.Message, "DriveLink", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        auth = new GoogleAuth(http, state, store);
        drive = new DriveService(http, auth.GetTokenAsync);
        history = new LocalFileHistory(state, store);
        tray = new TrayController(
            () => Dispatcher.Invoke(ShowFromTray),
            () => Dispatcher.Invoke(() => { ShowFromTray(); Settings_Click(this, new RoutedEventArgs()); }),
            () => Dispatcher.Invoke(() => { ShowFromTray(); exitRequested = true; Close(); }));
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && state.Settings.RunInBackground && !busy) HideToTray(); };
        Closed += (_, _) => tray.Dispose();
        SpacesList.PreviewMouseDown += (sender, e) => { if (e.ChangedButton == MouseButton.Right) Space_RightClick(sender, e); };
        uploads.Changed += UpdateControls;
        downloads.Changed += UpdateControls;
        speedTimer.Tick += (_, _) =>
        {
            if (uploads.Count == 0 || DateTimeOffset.UtcNow - lastUploadProgress > TimeSpan.FromSeconds(2)) UploadSpeed.Text = "↑ 0 B/s";
            if (downloads.Count == 0 || DateTimeOffset.UtcNow - lastDownloadProgress > TimeSpan.FromSeconds(2)) DownloadSpeed.Text = "↓ 0 B/s";
        };
        ReloadAccounts();
        poll.Tick += async (_, _) => await RefreshAsync(true);
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        ApplyUserSettings(); RenderFiles(); speedTimer.Start();
        if (Environment.GetCommandLineArgs().Contains("--background") && state.Settings.RunInBackground) HideToTray();
        await RefreshAsync(); poll.Start();
    }
    private void ApplyUserSettings()
    {
        tray.Visible = state.Settings.RunInBackground;
        var opacity = Math.Clamp(state.Settings.DropOverlayOpacity, 0, 1);
        DropOverlay.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), 237, 242, 255));
    }
    public void ShowFromTray() { Show(); ShowInTaskbar = true; if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void HideToTray() { tray.Visible = true; Hide(); ShowInTaskbar = false; }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (state.Settings.RunInBackground && !exitRequested && !busy) { e.Cancel = true; HideToTray(); return; }
        if ((busy || HasTransfers) && MessageBox.Show(this, "Một thao tác đang thực hiện. Đóng ứng dụng sẽ hủy thao tác đó. Tiếp tục đóng?", "Đóng DriveLink", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; exitRequested = false; return; }
        poll.Stop(); speedTimer.Stop(); lifetime.Cancel(); operation?.Cancel(); uploads.CancelAll(); downloads.CancelAll();
    }
    private void ReloadAccounts()
    {
        applying = true;
        AccountsBox.ItemsSource = null;
        AccountsBox.ItemsSource = state.Accounts;
        AccountsBox.SelectedItem = state.Accounts.FirstOrDefault(x => x.Id == state.SelectedAccountId) ?? state.Accounts.FirstOrDefault();
        applying = false;
    }
    private async void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (applying || !IsLoaded || demo) return;
        state.SelectedAccountId = CurrentAccount?.Id;
        try { store.Save(state); }
        catch (Exception ex) { ShowError(ex); }
        files = []; SpacesList.ItemsSource = null; RenderFiles();
        await RefreshAsync();
    }
    private async void Space_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (applying || !IsLoaded || demo) return;
        files = []; RenderFiles(); await RefreshAsync();
    }
    private void SetBusy(bool value)
    {
        busy = value;
        UpdateControls();
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        TransferProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        TransferProgress.IsIndeterminate = value;
        TransferProgress.Value = 0;
    }
    private void UpdateControls()
    {
        AccountsBox.IsEnabled = !busy && !refreshing && !HasTransfers && !demo;
        SpacesList.IsEnabled = !busy && !refreshing && !HasTransfers;
        CancelUploads.IsEnabled = uploads.Count > 0;
        CancelDownloads.IsEnabled = downloads.Count > 0;
    }
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (busy || refreshing || HasTransfers) { StatusLabel.Text = "Hãy chờ truyền file/thao tác hiện tại hoàn tất trước khi thay đổi kết nối hoặc không gian."; return; }
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetBusy(true);
        try { await work(operation.Token); }
        catch (OperationCanceledException) { StatusLabel.Text = "Đã hủy/chờ quá thời gian. Nếu đang gửi file, hãy làm mới để kiểm tra trạng thái trên Drive."; }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); operation.Dispose(); operation = null; }
    }
    private void ShowError(Exception ex)
    {
        StatusLabel.Text = "Thao tác chưa hoàn tất. " + ex.Message;
        if (!lifetime.IsCancellationRequested) MessageBox.Show(this, ex.Message, "DriveLink", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private bool Ready(bool space = true)
    {
        if (demo) { MessageBox.Show(this, "Đây là dữ liệu minh họa. Bấm Thoát xem thử rồi kết nối Google để sử dụng thật.", "Xem thử"); return false; }
        if (CurrentAccount is null) { MessageBox.Show(this, "Thêm tài khoản Google trước khi tiếp tục.", "Chưa kết nối"); return false; }
        if (space && CurrentSpace is null) { MessageBox.Show(this, "Tạo hoặc chọn một không gian chung trước.", "Chưa có không gian"); return false; }
        return true;
    }
    private async Task RefreshAsync(bool background = false)
    {
        if (busy || refreshing || demo || lifetime.IsCancellationRequested) return;
        if (CurrentAccount is not { } account)
        {
            SpaceTitle.Text = "Chia sẻ giữa các máy, thật đơn giản.";
            SpaceSubtitle.Text = "Kết nối Google để bắt đầu không gian chung của bạn.";
            StatusLabel.Text = "Chưa kết nối Google";
            return;
        }
        refreshing = true;
        UpdateControls();
        try
        {
            var spaceId = CurrentSpace?.Id;
            if (!HasTransfers && (!background || DateTimeOffset.UtcNow - lastSpaceRefresh > TimeSpan.FromMinutes(1)))
            {
                var spaces = SpaceOrganizer.Apply(await drive.ListSpacesAsync(account, lifetime.Token), state.SpacePreferences);
                applying = true;
                SpacesList.ItemsSource = spaces;
                SpacesList.SelectedItem = spaces.FirstOrDefault(x => x.Id == spaceId) ?? spaces.FirstOrDefault();
                applying = false;
                lastSpaceRefresh = DateTimeOffset.UtcNow;
            }
            if (CurrentSpace is { } space)
            {
                SpaceTitle.Text = space.ToString();
                SpaceSubtitle.Text = "Không gian riêng tư • Tự cập nhật file mỗi 15 giây khi ứng dụng đang mở";
                var result = await drive.ListFilesAsync(account, space.Id, lifetime.Token);
                files = result.OrderByDescending(x => x.CreatedAt).ToList();
                StatusLabel.Text = $"Đã cập nhật lúc {DateTime.Now:HH:mm:ss} · {account.Email}";
            }
            else
            {
                files = [];
                SpaceTitle.Text = "Tạo không gian chung đầu tiên";
                SpaceSubtitle.Text = "Hoặc nhờ tài khoản khác mời bạn vào thư mục có tên [DriveLink].";
                StatusLabel.Text = "Đã kết nối Google · Chưa có không gian chung";
            }
            poll.Start(); RenderFiles();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            poll.Stop();
            StatusLabel.Text = "Đã tạm dừng tự cập nhật: " + ex.Message + " Bấm ↻ để thử lại.";
            if (!background) ShowError(ex);
        }
        finally { applying = false; refreshing = false; UpdateControls(); }
    }
    private void RenderFiles()
    {
        if (FilesGrid is null) return;
        var selectedId = CurrentFile?.Id;
        IEnumerable<DriveItem> visible = files;
        if (filter == 1) visible = visible.Where(x => x.Owners.Any(o => o.Me));
        if (filter == 2) visible = visible.Where(x => !x.Owners.Any(o => o.Me));
        var search = SearchBox.Text.Trim();
        if (search.Length > 0) visible = visible.Where(x => x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || x.OwnerLabel.Contains(search, StringComparison.OrdinalIgnoreCase));
        var rows = visible.ToList();
        FilesGrid.ItemsSource = rows;
        FilesGrid.SelectedItem = rows.FirstOrDefault(x => x.Id == selectedId);
        CountLabel.Text = $"{rows.Count} mục";
        EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (search.Length > 0 || filter != 0) { EmptyTitle.Text = "Chưa có file phù hợp"; EmptyDescription.Text = "Thử từ khóa khác hoặc quay về File gần đây."; }
        else { EmptyTitle.Text = "Sẵn sàng cho file đầu tiên"; EmptyDescription.Text = CurrentSpace is null ? "Thêm tài khoản Google, tạo không gian và mời tài khoản trên máy còn lại để bắt đầu." : "Thả file vào bất kỳ đâu trong ứng dụng để gửi vào không gian đang chọn."; }
    }
    private void SetFilter(int value, string title)
    {
        filter = value; ListTitle.Text = title;
        var buttons = new[] { RecentButton, SentButton, ReceivedButton };
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Background = i == value ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 240, 255)) : System.Windows.Media.Brushes.White;
            buttons[i].Foreground = (System.Windows.Media.Brush)FindResource(i == value ? "Accent" : "Ink");
        }
        RenderFiles();
    }
    private void Recent_Click(object s, RoutedEventArgs e) => SetFilter(0, "File gần đây");
    private void Sent_Click(object s, RoutedEventArgs e) => SetFilter(1, "Tôi đã gửi");
    private void Received_Click(object s, RoutedEventArgs e) => SetFilter(2, "Từ người khác");
    private void Search_Changed(object s, TextChangedEventArgs e) { if (IsLoaded) RenderFiles(); }
    private void File_Changed(object s, SelectionChangedEventArgs e) => SelectionLabel.Text = CurrentFile is { } file ? file.Name : "Chọn file để tải về hoặc mở trên Drive.";
    private async void Refresh_Click(object s, RoutedEventArgs e) => await RefreshAsync();
    private void Cancel_Click(object s, RoutedEventArgs e) => operation?.Cancel();
    private void CancelUploads_Click(object s, RoutedEventArgs e) => uploads.CancelAll();
    private void CancelDownloads_Click(object s, RoutedEventArgs e) => downloads.CancelAll();

    private void Settings_Click(object s, RoutedEventArgs e)
    {
        if (busy || refreshing || HasTransfers) { StatusLabel.Text = "Chờ truyền file hoàn tất trước khi thay đổi tài khoản."; return; }
        poll.Stop();
        var dialog = new SettingsWindow(state, store) { Owner = this };
        dialog.ShowDialog();
        ApplyUserSettings();
        poll.Start();
        if (demo) return;
        ReloadAccounts(); files = []; SpacesList.ItemsSource = null; RenderFiles();
        _ = RefreshAsync();
    }
    private async void Connect_Click(object s, RoutedEventArgs e)
    {
        if (busy || refreshing || HasTransfers) { StatusLabel.Text = "Chờ thao tác hiện tại hoàn tất trước khi thêm tài khoản."; return; }
        if (demo) { Ready(); return; }
        if (string.IsNullOrWhiteSpace(state.Config.ClientId))
        {
            Settings_Click(s, e);
            if (string.IsNullOrWhiteSpace(state.Config.ClientId)) return;
        }
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetBusy(true);
        var login = new LoginStatusWindow(() => operation?.Cancel()) { Owner = this };
        login.Show();
        try
        {
            StatusLabel.Text = "Đang chờ bạn đăng nhập Google trong trình duyệt…";
            var account = await auth.ConnectAsync(operation.Token);
            ReloadAccounts();
            StatusLabel.Text = "Đã kết nối Google: " + account.Email;
            await login.CompleteAsync(true, "Đã liên kết " + account.Email + ". Bạn có thể gửi và nhận file.");
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Đăng nhập đã bị hủy hoặc quá thời gian chờ.";
            await login.CompleteAsync(false, "Yêu cầu đã bị hủy hoặc quá thời gian chờ. Bạn có thể thử lại trong ứng dụng.");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Đăng nhập không thành công: " + ex.Message;
            await login.CompleteAsync(false, ex.Message);
        }
        finally { SetBusy(false); operation.Dispose(); operation = null; }
        await RefreshAsync();
    }
    private async void CreateSpace_Click(object s, RoutedEventArgs e)
    {
        if (busy || refreshing || HasTransfers) return;
        if (!Ready(false)) return;
        var name = PromptWindow.Ask(this, "Tạo không gian chung", "Đặt tên để dễ nhận ra trên cả hai máy", "Ví dụ: File giữa hai máy");
        if (string.IsNullOrWhiteSpace(name)) return;
        var account = CurrentAccount!;
        Space? created = null;
        await RunAsync(async ct => { created = await drive.CreateSpaceAsync(account, name.Trim(), ct); StatusLabel.Text = "Đã tạo không gian. Mời tài khoản trên máy còn lại bằng nút Thành viên."; });
        await RefreshAsync();
        if (created is not null && SpacesList.ItemsSource is List<Space> spaces) SpacesList.SelectedItem = spaces.FirstOrDefault(x => x.Id == created.Id);
    }
    private void Space_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (busy || refreshing || HasTransfers || demo) return;
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListBoxItem)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        if (element is not ListBoxItem { DataContext: Space space } row) return;
        e.Handled = true;
        var menu = new ContextMenu();
        var pin = new MenuItem { Header = space.Pinned ? "Bỏ ghim" : "Ghim lên đầu" };
        pin.Click += (_, _) => EditSpacePreference(space, p => p.Pinned = !p.Pinned);
        menu.Items.Add(pin);
        var colors = new MenuItem { Header = "Đánh dấu bằng màu" };
        foreach (var (name, color) in new[] { ("Xanh dương", "#3865F3"), ("Xanh lá", "#168464"), ("Tím", "#8657D6"), ("Cam", "#E58B29"), ("Đỏ", "#D34655"), ("Hồng", "#D25395"), ("Xám", "#718098") })
        {
            var choice = new MenuItem { Header = name, Icon = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = (Brush)new BrushConverter().ConvertFromString(color)! } };
            choice.Click += (_, _) => EditSpacePreference(space, p => p.Color = color);
            colors.Items.Add(choice);
        }
        menu.Items.Add(colors);
        var hide = new MenuItem { Header = "Ẩn khỏi danh sách trên máy này" };
        hide.Click += (_, _) => { EditSpacePreference(space, p => p.Hidden = true); _ = RefreshAsync(); };
        menu.Items.Add(hide); menu.Items.Add(new Separator());
        var trash = new MenuItem { Header = "Xóa không gian → Thùng rác Drive…", IsEnabled = space.Capabilities.CanTrash, ToolTip = "Cần quyền xóa thư mục trên Google Drive." };
        trash.Click += async (_, _) =>
        {
            if (busy || refreshing || HasTransfers || CurrentAccount is not { } account) return;
            if (MessageBox.Show(this, $"Đưa thư mục không gian “{space}” vào Thùng rác Google Drive? Thành viên có thể mất quyền truy cập không gian và các file bên trong. Bạn có thể khôi phục thư mục trong Thùng rác Drive. File đã tải về máy được giữ nguyên.", "Xóa không gian", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await RunAsync(async ct => { await drive.TrashSpaceAsync(account, space.Id, ct); StatusLabel.Text = "Đã đưa không gian vào Thùng rác Drive."; });
            await RefreshAsync();
        };
        menu.Items.Add(trash); row.ContextMenu = menu; menu.PlacementTarget = row; menu.IsOpen = true;
    }
    private void EditSpacePreference(Space space, Action<SpacePreference> edit)
    {
        if (!state.SpacePreferences.TryGetValue(space.Id, out var preference)) state.SpacePreferences[space.Id] = preference = new();
        edit(preference);
        try
        {
            store.Save(state);
            var selectedId = CurrentSpace?.Id;
            var spaces = SpaceOrganizer.Apply(SpacesList.Items.Cast<Space>(), state.SpacePreferences);
            applying = true; SpacesList.ItemsSource = spaces; SpacesList.SelectedItem = spaces.FirstOrDefault(x => x.Id == selectedId) ?? spaces.FirstOrDefault(); applying = false;
            StatusLabel.Text = "Đã lưu màu/ghim trên máy này.";
        }
        catch (Exception ex) { applying = false; ShowError(ex); }
    }
    private void CopySpace_Click(object s, RoutedEventArgs e)
    {
        if (!Ready()) return;
        try { Clipboard.SetText("https://drive.google.com/drive/folders/" + CurrentSpace!.Id); StatusLabel.Text = "Đã sao chép liên kết. Người nhận vẫn cần được cấp quyền trong Thành viên."; }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Members_Click(object s, RoutedEventArgs e)
    {
        if (!Ready() || busy || refreshing || HasTransfers) return;
        poll.Stop();
        var dialog = new MembersWindow(drive, CurrentAccount!, CurrentSpace!) { Owner = this };
        dialog.ShowDialog();
        await RefreshAsync(); poll.Start();
    }
    private async void Upload_Click(object s, RoutedEventArgs e)
    {
        if (!Ready() || busy) return;
        var picker = new OpenFileDialog { Multiselect = true, Title = "Chọn file gửi vào không gian chung" };
        if (picker.ShowDialog(this) == true) await UploadPathsAsync(picker.FileNames);
    }
    private async void UploadFolder_Click(object s, RoutedEventArgs e)
    {
        if (!Ready() || busy) return;
        var picker = new OpenFolderDialog { Title = "Chọn thư mục để nén rồi gửi", Multiselect = true };
        if (picker.ShowDialog(this) == true) await UploadPathsAsync(picker.FolderNames);
    }
    private void Upload_DragOver(object s, DragEventArgs e)
    {
        var canSend = !busy && !demo && CurrentAccount is not null && CurrentSpace is not null && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = canSend ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = canSend && state.Settings.ShowDropOverlay ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }
    private void Upload_DragLeave(object s, DragEventArgs e)
    {
        var point = e.GetPosition(this);
        if (point.X <= 0 || point.Y <= 0 || point.X >= ActualWidth || point.Y >= ActualHeight)
            DropOverlay.Visibility = Visibility.Collapsed;
    }
    private async void Upload_Drop(object s, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (!Ready() || busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await UploadPathsAsync(paths);
    }
    private async Task UploadPathsAsync(string[] paths)
    {
        if (!Ready() || busy || lifetime.IsCancellationRequested) return;
        var account = CurrentAccount!; var space = CurrentSpace!;
        var tasks = paths.Select(path => ObserveTransferAsync(uploads.Enqueue(async laneToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(laneToken, lifetime.Token);
            var version = ++uploadVersion;
            UploadProgress.Value = 0;
            var name = Path.GetFileName(path);
            UploadLabel.Text = "Đang gửi: " + name;
            var progress = new Progress<TransferProgress>(value =>
            {
                if (version != uploadVersion || lifetime.IsCancellationRequested) return;
                lastUploadProgress = DateTimeOffset.UtcNow;
                UploadProgress.Value = value.Percent;
                UploadSpeed.Text = "↑ " + value.SpeedLabel;
                UploadLabel.Text = $"{name} · {value.AmountLabel} · {value.Percent:0}% · {uploads.Count} file trong hàng đợi";
            });
            try
            {
                UploadProgress.IsIndeterminate = Directory.Exists(path);
                using var packed = Directory.Exists(path)
                    ? await compressor.PackAsync(path, state.Settings, new Progress<string>(text => { if (version == uploadVersion) UploadLabel.Text = text; }), linked.Token)
                    : null;
                var uploadPath = packed?.ArchivePath ?? path;
                UploadProgress.IsIndeterminate = false;
                name = Path.GetFileName(uploadPath);
                var id = await drive.UploadAsync(account, space.Id, uploadPath, progress, linked.Token);
                RememberLocalFile(id, path);
                UploadLabel.Text = "Đã gửi: " + name + (packed is null ? "" : $" · {DriveItem.FormatSize(packed.OriginalBytes)} → {DriveItem.FormatSize(packed.ArchiveBytes)}");
                UploadProgress.Value = 100;
                await RefreshAsync(true);
            }
            catch (OperationCanceledException) { UploadLabel.Text = "Đã hủy gửi. Kiểm tra Drive trước khi gửi lại."; throw; }
            catch { UploadLabel.Text = "Gửi chưa hoàn tất: " + name; throw; }
            finally { ++uploadVersion; UploadSpeed.Text = "↑ 0 B/s"; UploadProgress.IsIndeterminate = false; }
        }), "gửi")).ToArray();
        await Task.WhenAll(tasks);
        await RefreshAsync();
    }
    private async Task ObserveTransferAsync(Task task, string direction)
    {
        try { await task; }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) StatusLabel.Text = "Đã hủy hàng đợi " + direction + "."; }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Download_Click(object s, RoutedEventArgs e)
    {
        if (!Ready() || busy || lifetime.IsCancellationRequested || CurrentFile is not { } file) return;
        if (file.MimeType.StartsWith("application/vnd.google-apps.")) { MessageBox.Show(this, "Dùng Mở trên Drive cho thư mục và tài liệu Google. Bản đầu chưa hỗ trợ xuất tài liệu Google.", "Tải về"); return; }
        var safeName = string.Concat(file.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var picker = new SaveFileDialog { FileName = safeName, Title = "Lưu file", OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        var target = Path.GetFullPath(picker.FileName);
        if (!downloadTargets.Add(target)) { StatusLabel.Text = "File đích này đang được tải hoặc đã nằm trong hàng đợi."; return; }
        var account = CurrentAccount!;
        try
        {
            await ObserveTransferAsync(downloads.Enqueue(async laneToken =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(laneToken, lifetime.Token);
                var version = ++downloadVersion;
                DownloadProgress.IsIndeterminate = false; DownloadProgress.Value = 0;
                DownloadLabel.Text = "Đang nhận: " + file.Name;
                var progress = new Progress<TransferProgress>(value =>
                {
                    if (version != downloadVersion || lifetime.IsCancellationRequested) return;
                    lastDownloadProgress = DateTimeOffset.UtcNow;
                    DownloadProgress.IsIndeterminate = !value.TotalBytes.HasValue;
                    DownloadProgress.Value = value.Percent;
                    DownloadSpeed.Text = "↓ " + value.SpeedLabel;
                    DownloadLabel.Text = $"{file.Name} · {value.AmountLabel} · {downloads.Count} file trong hàng đợi";
                });
                try
                {
                    await drive.DownloadAsync(account, file, target, linked.Token, progress);
                    RememberLocalFile(file.Id, target);
                    DownloadProgress.IsIndeterminate = false; DownloadProgress.Value = 100;
                    DownloadLabel.Text = "Đã nhận: " + file.Name;
                    StatusLabel.Text = "Đã tải xong. Nhấn vào file trong danh sách để mở thư mục trên máy.";
                }
                catch (OperationCanceledException) { DownloadLabel.Text = "Đã hủy nhận: " + file.Name; throw; }
                catch { DownloadLabel.Text = "Nhận chưa hoàn tất: " + file.Name; throw; }
                finally { ++downloadVersion; DownloadSpeed.Text = "↓ 0 B/s"; DownloadProgress.IsIndeterminate = false; }
            }), "nhận");
        }
        finally { downloadTargets.Remove(target); }
    }
    private void OpenFile_Click(object s, RoutedEventArgs e)
    {
        if (!Ready() || CurrentFile is not { } file) return;
        try { Process.Start(new ProcessStartInfo(file.BrowserUrl) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void FileRow_Click(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not DataGridRow)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        if (element is DataGridRow { Item: DriveItem item }) OpenLocalFolder(item);
    }
    private void FileRow_RightClick(object s, MouseButtonEventArgs e)
    {
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not DataGridRow)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        if (element is not DataGridRow { Item: DriveItem item } row) return;
        FilesGrid.SelectedItem = item;
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Sao chép link mở trên web", CommandParameter = item };
        var local = new MenuItem { Header = "Mở thư mục trên máy", CommandParameter = item };
        copy.Click += CopyFileLink_Click;
        local.Click += OpenLocalFolder_Click;
        menu.Items.Add(copy); menu.Items.Add(local);
        row.ContextMenu = menu;
    }
    private async void OpenLocalFolder(DriveItem item)
    {
        if (demo) { StatusLabel.Text = "Dữ liệu minh họa chưa có file trên máy."; return; }
        var path = history.FindKnownPath(item.Id);
        if (path is null) { StatusLabel.Text = "File chưa có trên máy hoặc đã di chuyển. Bấm Tải về để lưu bản cục bộ."; return; }
        if (folderOpening) return;
        folderOpening = true;
        try
        {
            StatusLabel.Text = "Đang yêu cầu mở thư mục chứa: " + item.Name;
            await ShellFileNavigator.OpenContainingFolderAsync(path);
            StatusLabel.Text = "Đã yêu cầu File Explorer mở thư mục chứa: " + item.Name;
        }
        catch (FileNotFoundException ex) { StatusLabel.Text = ex.Message; }
        catch (Exception ex) { ShowError(ex); }
        finally { folderOpening = false; }
    }
    private void RememberLocalFile(string id, string path)
    {
        try { history.Record(id, path); }
        catch (Exception ex) { StatusLabel.Text = "File đã truyền xong, nhưng chưa lưu được đường dẫn trên máy: " + ex.Message; }
    }
    private void CopyFileLink_Click(object s, RoutedEventArgs e)
    {
        if (demo) { StatusLabel.Text = "Link minh họa không phải file thật trên Google Drive."; return; }
        if (s is not MenuItem { CommandParameter: DriveItem item }) return;
        try { Clipboard.SetText(item.BrowserUrl); StatusLabel.Text = "Đã sao chép link mở trên web. Quyền truy cập file được giữ nguyên."; }
        catch (Exception ex) { ShowError(ex); }
    }
    private void OpenLocalFolder_Click(object s, RoutedEventArgs e)
    {
        if (s is MenuItem { CommandParameter: DriveItem item }) OpenLocalFolder(item);
    }
    private async void Demo_Click(object s, RoutedEventArgs e)
    {
        if (busy || refreshing || HasTransfers) return;
        demo = !demo; DemoButton.Content = demo ? "Thoát xem thử" : "Xem thử giao diện";
        AccountsBox.IsEnabled = !demo;
        if (!demo) { files = []; SpacesList.ItemsSource = null; RenderFiles(); await RefreshAsync(); return; }
        applying = true;
        var space = new Space { Id = "demo", Name = "[DriveLink] File giữa hai máy" };
        SpacesList.ItemsSource = new[] { space }; SpacesList.SelectedItem = space;
        applying = false;
        SpaceTitle.Text = "File giữa hai máy · XEM THỬ";
        SpaceSubtitle.Text = "Dữ liệu minh họa • Chưa kết nối Google • Không gửi hoặc tải file thật";
        files = [
            new() { Id = "1", Name = "Bản thiết kế giao diện.pdf", MimeType = "application/pdf", Size = 2456789, CreatedAt = DateTimeOffset.Now.AddMinutes(-3), Owners = [new() { Email = "may-a@example.com", Me = true }] },
            new() { Id = "2", Name = "Ảnh tham khảo.zip", MimeType = "application/zip", Size = 38765432, CreatedAt = DateTimeOffset.Now.AddMinutes(-18), Owners = [new() { Email = "may-b@example.com" }] },
            new() { Id = "3", Name = "Kế hoạch công việc.xlsx", MimeType = "application/octet-stream", Size = 124567, CreatedAt = DateTimeOffset.Now.AddHours(-1), Owners = [new() { Email = "may-b@example.com" }] },
            new() { Id = "4", Name = "Ghi chú dự án.txt", MimeType = "text/plain", Size = 2546, CreatedAt = DateTimeOffset.Now.AddHours(-2), Owners = [new() { Email = "may-a@example.com", Me = true }] }
        ];
        StatusLabel.Text = "Đang xem thử giao diện. Dữ liệu này không nằm trên Google Drive.";
        RenderFiles();
    }
}
