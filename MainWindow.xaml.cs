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
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Globalization;
using DriveLink.Services;
using DriveLink.Ui;

namespace DriveLink;
public partial class MainWindow : Window
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly bool preview;
    private readonly SecureStore store;
    private readonly SavedState state;
    private readonly GoogleAuth auth;
    private readonly DriveService drive;
    private readonly LocalFileHistory history;
    private readonly FolderCompressor compressor = new();
    private readonly TrayController tray;
    private readonly AppUpdateFlow appUpdates;
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private UpdateWindow? updateWindow;
    private bool installingUpdate;
    private bool resumePoll, resumeSpeed, resumeUpdates;
    private bool exitRequested, folderOpening;
    private readonly TransferCenter transfers;
    private readonly InboxWatcher inbox;
    private readonly E2eKeys e2eKeys;
    private Services.Lan.LanService? lan;
    private Services.Lan.LanTrustStore? lanTrust;
    private readonly DispatcherTimer watchTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer notifyTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private readonly Dictionary<string, (DateTimeOffset At, List<Space> Spaces)> spaceCache = [];
    private readonly Dictionary<string, Account> downloadAccounts = [];
    private readonly List<(IncomingFile File, bool Auto, bool Risky)> incoming = [];
    private bool watching;
    private int tuneTicks;
    private readonly HashSet<string> downloadTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer speedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool HasTransfers => transfers.Uploads.Count > 0 || transfers.Downloads.Count > 0;
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private List<DriveItem> files = [];
    private bool busy, refreshing, applying, demo;
    private int filter;
    private DateTimeOffset lastSpaceRefresh;
    private readonly CrossAccountCopier copier;
    private readonly GrantJournal grantJournal;
    private readonly bool motion = SystemParameters.ClientAreaAnimation && !Ui.PerformanceMode.IsLite;
    private readonly DispatcherTimer islandTimer = new();
    private readonly List<double> upHistory = [], downHistory = [];
    private double lastUploadBps, lastDownloadBps;
    private Account? crossTarget;
    private bool crossRunning, dropActive, ambientRunning;
    private const int SparkPoints = 40;
    private const string FolderMime = "application/vnd.google-apps.folder";
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private enum IslandKind { Busy, Done, Warn }
    private Account? CurrentAccount => AccountsBox.SelectedItem as Account;
    private Space? CurrentSpace => SpacesList.SelectedItem as Space;
    private DriveItem? CurrentFile => FilesGrid.SelectedItem as DriveItem;
    private List<DriveItem> SelectedFiles => FilesGrid.SelectedItems.OfType<DriveItem>().ToList();
    private readonly Dictionary<string, string> lastCrossFolder = [];

    public MainWindow() : this(null, null, false) { }
    public MainWindow(SavedState? initialState, SecureStore? localStore, bool preview, IAppUpdateBackend? updateBackend = null)
    {
        this.preview = preview;
        appUpdates = new(updateBackend ?? new VelopackUpdateBackend());
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width); MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width); Height = Math.Min(Height, workArea.Height);
        FilesGrid.PreviewMouseRightButtonDown += FileRow_RightClick;
        FilesGrid.PreviewKeyDown += (_, e) =>
        {
            if ((e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) && CurrentFile is { } file)
            {
                var menu = BuildFileMenu(file); menu.PlacementTarget = FilesGrid; menu.IsOpen = true; e.Handled = true;
            }
        };
        store = localStore ?? new SecureStore();
        try { state = initialState ?? store.Load(); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or IOException)
        {
            state = new();
            MessageBox.Show("Không đọc được tài khoản đã lưu. Dữ liệu hiện có chưa bị ghi đè; hãy sao lưu accounts.dat trước khi kết nối lại.\n" + ex.Message, "DriveLink", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        auth = new GoogleAuth(http, state, store);
        drive = new DriveService(http, auth.GetTokenAsync);
        grantJournal = new GrantJournal(store.Folder);
        copier = new CrossAccountCopier(drive, grantJournal);
        e2eKeys = new E2eKeys(preview ? Path.Combine(Path.GetTempPath(), "DriveLink-preview") : store.Folder);
        transfers = new TransferCenter(drive, compressor, () => state.Settings, preview ? Path.Combine(Path.GetTempPath(), "DriveLink-preview") : store.Folder) { Keys = e2eKeys };
        transfers.Changed += () => Dispatcher.BeginInvoke(() => { UpdateControls(); RenderTransfers(); });
        transfers.Completed += item => Dispatcher.BeginInvoke(() => OnTransferFinished(item));
        refreshAfterUpload.Tick += async (_, _) => { refreshAfterUpload.Stop(); await RefreshAsync(true); };
        inbox = new InboxWatcher(drive, preview ? Path.Combine(Path.GetTempPath(), "DriveLink-preview") : store.Folder) { SpacesOf = a => spaceCache.TryGetValue(a.Id, out var cached) ? cached.Spaces : [] };
        inbox.Arrived += OnIncoming;
        auth.ReauthRequired += account => Dispatcher.BeginInvoke(() => OfferReauth(account));
        watchTimer.Tick += async (_, _) => await WatchAsync();
        notifyTimer.Tick += (_, _) => { notifyTimer.Stop(); FlushIncoming(); };
        history = new LocalFileHistory(state, store);
        GlassCard.Motion = motion;
        for (var i = 0; i < SparkPoints; i++) { upHistory.Add(0); downHistory.Add(0); }
        islandTimer.Tick += (_, _) => { islandTimer.Stop(); HideIsland(); };
        StateChanged += (_, _) =>
        {
            // Cửa sổ tự vẽ khung bị tràn mép khi phóng to; chừa lề bằng độ dày khung thay đổi kích thước.
            var maximized = WindowState == WindowState.Maximized;
            var frame = SystemParameters.WindowResizeBorderThickness;
            WindowRoot.Margin = maximized ? new Thickness(frame.Left + 3, frame.Top + 3, frame.Right + 3, frame.Bottom + 3) : new Thickness(0);
            MaxGlyph.Text = maximized ? "" : "";
            SetAmbient(WindowState != WindowState.Minimized && IsVisible);
        };
        IsVisibleChanged += (_, _) => SetAmbient(IsVisible && WindowState != WindowState.Minimized);
        tray = new TrayController(
            () => Dispatcher.Invoke(ShowFromTray),
            () => Dispatcher.Invoke(() => { ShowFromTray(); Settings_Click(this, new RoutedEventArgs()); }),
            () => Dispatcher.Invoke(() => { ShowFromTray(); exitRequested = true; Close(); }),
            () => Dispatcher.Invoke(() => { ShowFromTray(); Queue_Click(this, new RoutedEventArgs()); }));
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) return;
            if (state.Settings.RunInBackground && !busy) HideToTray();
            else if (Ui.PerformanceMode.IsLite) Ui.PerformanceMode.TrimMemory();
        };
        Closed += (_, _) => tray.Dispose();
        SpacesList.PreviewMouseDown += (sender, e) => { if (e.ChangedButton == MouseButton.Right) Space_RightClick(sender, e); };
        speedTimer.Tick += (_, _) =>
        {
            RenderTransfers();
            if (++tuneTicks % 10 == 0) transfers.Tune();
            upHistory.Add(lastUploadBps); downHistory.Add(lastDownloadBps);
            if (upHistory.Count > SparkPoints) { upHistory.RemoveAt(0); downHistory.RemoveAt(0); }
            DrawSpark();
        };
        ReloadAccounts();
        poll.Tick += async (_, _) => await RefreshAsync(true);
        appUpdates.Changed += () => { if (!lifetime.IsCancellationRequested) Dispatcher.BeginInvoke(RenderUpdateNotice); };
        updateTimer.Tick += async (_, _) => { if (state.Settings.CheckUpdatesOnStartup) await appUpdates.CheckAsync(lifetime.Token); };
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (preview)
        {
            demo = true; ApplyUserSettings();
            var space = new Space { Id = "preview", Name = "[DriveLink] Làm việc" };
            applying = true; SpacesList.ItemsSource = new[] { space }; SpacesList.SelectedItem = space; applying = false;
            SpaceTitle.Text = "Làm việc"; SpaceSubtitle.Text = "Thả file hoặc thư mục ở bất kỳ đâu · Tự làm mới mỗi 15 giây";
            files = Enumerable.Range(1, 45).Select(i => new DriveItem { Id = "preview-" + i, Name = i % 3 == 0 ? $"Bản thiết kế không gian làm việc — phiên bản {i}.pdf" : $"Tài liệu chia sẻ {i:00}.zip", MimeType = "application/octet-stream", Size = i * 1728000, CreatedAt = DateTimeOffset.Now.AddMinutes(-i * 5), Owners = [new() { Email = i % 2 == 0 ? "maynhan@example.com" : "quanghuy.demo@example.com", Me = i % 2 == 1 }], Capabilities = new() { CanCopy = true, CanDownload = true, CanShare = true, CanRename = true, CanTrash = true, CanModifyEditorContentRestriction = true } }).ToList();
            RenderUpdatedStatus(CurrentAccount?.Email ?? "maygui@example.com");
            RenderSpaceHeader(); RenderQuota(835628007219, 2199023255552, CurrentAccount);
            RenderFiles(); UpdateCrossPanel(); PlayEntrance();
            await TrimAfterStartupAsync(); return;
        }
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        ApplyUserSettings(); RenderFiles(); UpdateCrossPanel(); speedTimer.Start();
        PlayEntrance(); SetAmbient(true);
        if (Environment.GetCommandLineArgs().Contains("--background") && state.Settings.RunInBackground) HideToTray();
        if (state.Settings.CheckUpdatesOnStartup) _ = appUpdates.CheckAsync(lifetime.Token);
        updateTimer.Start();
        await ProtectTokensAsync();
        await RefreshAsync(); poll.Start();
        OfferResumeTransfers();
        foreach (var expired in state.Accounts.Where(a => a.NeedsReauth).ToList()) OfferReauth(expired);
        watchTimer.Start(); _ = WatchAsync();
        if (state.Settings.LanEnabled) StartLan();
        await RecoverTemporaryGrantsAsync();
        await TrimAfterStartupAsync();
    }
    /// <summary>Chế độ nhẹ: sau khi tải xong, trả lại bộ nhớ chỉ dùng lúc khởi động (dựng giao diện, đọc dữ liệu).</summary>
    private async Task TrimAfterStartupAsync()
    {
        if (!Ui.PerformanceMode.IsLite) return;
        await Task.Delay(TimeSpan.FromSeconds(4));
        Ui.PerformanceMode.TrimMemory();
    }
    /// <summary>Token lưu từ bản cũ được mã hóa lại bằng Windows Hello (hoặc khóa riêng trên máy). Người dùng hủy Hello thì để lần sau.</summary>
    private async Task ProtectTokensAsync()
    {
        try { await auth.ProtectStoredTokensAsync(lifetime.Token); }
        catch (TokenLockedException ex) { StatusLabel.Text = ex.Message; }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException) { StatusLabel.Text = "Chưa mã hóa lại được token đã lưu: " + ex.Message; }
    }
    /// <summary>Thu hồi quyền tạm còn sót khi lần trước app bị tắt ngang giữa lúc gửi chéo, và dọn thư mục tạm của lần tải qua máy.</summary>
    private async Task RecoverTemporaryGrantsAsync()
    {
        if (preview || lifetime.IsCancellationRequested) return;
        copier.CleanRelayLeftovers();
        if (grantJournal.Pending().Count == 0) return;
        try
        {
            var recovered = await copier.RecoverAsync(id => state.Accounts.FirstOrDefault(x => x.Id == id), lifetime.Token);
            var left = grantJournal.Pending().Count;
            if (recovered > 0) ShowIsland(IslandKind.Done, $"Đã thu hồi {recovered} quyền tạm còn sót", "Từ lần gửi chéo bị gián đoạn trước đó", 420, 5);
            if (left > 0) ShowIsland(IslandKind.Warn, $"Còn {left} quyền tạm chưa thu hồi được", "DriveLink sẽ thử lại ở lần mở sau; xem Nhật ký bảo mật trong Cài đặt", 500, 7);
        }
        catch (Exception ex) when (ex is TokenLockedException or OperationCanceledException or HttpRequestException) { }
    }
    private void ApplyUserSettings()
    {
        ApplyZoom();
        tray.Visible = state.Settings.RunInBackground;
        if (state.Settings.StartWithWindows && StartupRegistration.IsEnabled())
        {
            try { StartupRegistration.SetEnabled(true); } catch (Exception ex) { StatusLabel.Text = "Chưa cập nhật được đường dẫn khởi động Windows: " + ex.Message; }
        }
        var opacity = Math.Clamp(state.Settings.DropOverlayOpacity, 0, 1);
        DropOverlay.Background = new SolidColorBrush(Color.FromArgb((byte)(40 + 200 * opacity), 10, 15, 30));
    }
    public void ShowFromTray() { Show(); ShowInTaskbar = true; if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void HideToTray() { tray.Visible = true; Hide(); ShowInTaskbar = false; Ui.PerformanceMode.TrimMemory(); }
    private void RenderUpdateNotice()
    {
        if (lifetime.IsCancellationRequested) return;
        UpdateNotice.Visibility = UpdateDot.Visibility = appUpdates.Available is not null ? Visibility.Visible : Visibility.Collapsed;
        if (appUpdates.Available is { } update)
        {
            UpdateNoticeText.Text = $"Có bản {update.Version}";
            UpdateNotice.ToolTip = $"Có DriveLink {update.Version} mới. Xem thay đổi và cập nhật khi bạn sẵn sàng.";
        }
    }
    private void Updates_Click(object sender, RoutedEventArgs e) => OpenUpdates(this);
    private void OpenUpdates(Window owner)
    {
        if (updateWindow is not null) { updateWindow.Activate(); return; }
        updateWindow = new UpdateWindow(appUpdates, PrepareUpdateRestart, RestoreAfterUpdateFailure) { Owner = owner };
        updateWindow.Closed += (_, _) => updateWindow = null;
        updateWindow.ShowDialog();
    }
    private bool PrepareUpdateRestart()
    {
        if (busy || refreshing || HasTransfers || lifetime.IsCancellationRequested) return false;
        store.Save(state);
        resumePoll = poll.IsEnabled; resumeSpeed = speedTimer.IsEnabled; resumeUpdates = updateTimer.IsEnabled;
        installingUpdate = true; exitRequested = true;
        _ = lan?.StopAsync();
        poll.Stop(); watchTimer.Stop(); speedTimer.Stop(); updateTimer.Stop(); IsEnabled = false;
        return true;
    }
    private void RestoreAfterUpdateFailure()
    {
        if (!installingUpdate) return;
        installingUpdate = false; exitRequested = false; IsEnabled = true;
        if (!lifetime.IsCancellationRequested) { if (resumePoll) poll.Start(); if (resumeSpeed) speedTimer.Start(); if (resumeUpdates) updateTimer.Start(); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (state.Settings.RunInBackground && !exitRequested && !busy) { e.Cancel = true; HideToTray(); return; }
        if ((busy || HasTransfers) && MessageBox.Show(this, "Một thao tác đang thực hiện. Đóng ứng dụng sẽ hủy thao tác đó. Tiếp tục đóng?", "Đóng DriveLink", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; exitRequested = false; return; }
        poll.Stop(); speedTimer.Stop(); updateTimer.Stop(); lifetime.Cancel(); operation?.Cancel(); transfers.Shutdown();
    }
    private void ReloadAccounts()
    {
        applying = true;
        AccountsBox.ItemsSource = null;
        AccountsBox.ItemsSource = state.Accounts;
        AccountsBox.SelectedItem = state.Accounts.FirstOrDefault(x => x.Id == state.SelectedAccountId) ?? state.Accounts.FirstOrDefault();
        UpdateAccountMenu();
        applying = false;
    }
    private void UpdateAccountMenu()
    {
        if (CurrentAccount is { } account)
        {
            AccountMenuText.Text = DisplayPrivacy.MaskEmail(account.Email) + (account.NeedsReauth ? " · cần đăng nhập lại" : "");
            AccountAvatarText.Text = account.Email.Length > 0 ? char.ToUpperInvariant(account.Email[0]).ToString() : "?";
            AccountMenuButton.ToolTip = account.NeedsReauth ? "Phiên Google đã hết hạn · mở menu → Đăng nhập lại" : DisplayPrivacy.MaskEmail(account.Email);
        }
        else
        {
            AccountMenuText.Text = "Thêm tài khoản";
            AccountAvatarText.Text = "+";
            AccountMenuButton.ToolTip = "Thêm tài khoản Google";
        }
        UpdateCrossPanel();
    }
    private void AccountsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (state.Accounts.Count == 0) { Connect_Click(sender, e); return; }
        var menu = new ContextMenu { LayoutTransform = new ScaleTransform(UiPresentation.Scale, UiPresentation.Scale) };
        foreach (var expired in state.Accounts.Where(a => a.NeedsReauth).ToList())
        {
            var again = new MenuItem { Header = "Đăng nhập lại " + DisplayPrivacy.MaskEmail(expired.Email) + "…", FontWeight = FontWeights.SemiBold };
            again.Click += async (_, _) => await ConnectCoreAsync(expired);
            menu.Items.Add(again);
        }
        if (state.Accounts.Any(a => a.NeedsReauth)) menu.Items.Add(new Separator());
        foreach (var account in state.Accounts)
        {
            var entry = new MenuItem { Header = DisplayPrivacy.MaskEmail(account.Email), IsCheckable = true, IsChecked = CurrentAccount?.Id == account.Id };
            entry.Click += (_, _) =>
            {
                if (busy || refreshing || HasTransfers) { StatusLabel.Text = "Chờ truyền file/thao tác hiện tại trước khi đổi tài khoản."; return; }
                AccountsBox.SelectedItem = account;
            };
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "＋ Thêm tài khoản Google" }; add.Click += Connect_Click; menu.Items.Add(add);
        AccountMenuButton.ContextMenu = menu; menu.PlacementTarget = AccountMenuButton; menu.IsOpen = true;
    }
    private void ApplyZoom()
    {
        var area = SystemParameters.WorkArea;
        var zoom = DisplayPrivacy.ResolveScale(state.Settings.UiScale, area.Width, area.Height);
        UiScaleTransform.ScaleX = zoom; UiScaleTransform.ScaleY = zoom; UiPresentation.Scale = zoom;
        ZoomLabel.Content = (zoom * 100).ToString("0", Vi) + "%";
        ZoomLabel.ToolTip = state.Settings.UiScale == 0 ? "Đang tự điều chỉnh theo màn hình" : "Tự điều chỉnh theo màn hình (Ctrl + 0)";
        // Thanh trên (lề 12 + cao 52) là vùng kéo cửa sổ; co giãn theo tỷ lệ thu phóng.
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = 64 * zoom;
        ApplyLayout((ActualWidth > 0 ? ActualWidth : Width) / zoom, (ActualHeight > 0 ? ActualHeight : Height) / zoom);
        MinHeight = Math.Min(area.Height, zoom > 1.2 ? 580 : 480);
        UpdateAccountMenu();
    }
    /// <summary>
    /// Bố cục Bento theo độ rộng thực (đã chia tỷ lệ thu phóng):
    /// rộng → hai cột widget; vừa → một cột (gửi tệp, truyền tải, không gian); hẹp → một cột gọn.
    /// Gửi chéo vẫn dùng được từ menu chuột phải khi widget bị ẩn.
    /// </summary>
    private void ApplyLayout(double width, double height)
    {
        var wide = width >= 1260;
        var narrow = width < 940;
        SideGrid.Width = wide ? 600 : narrow ? 262 : 316;
        SideColB.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SideGrid.MinHeight = wide ? 600 : 640;
        static void Place(UIElement card, int row, int column, int rowSpan) { Grid.SetRow(card, row); Grid.SetColumn(card, column); Grid.SetRowSpan(card, rowSpan); }
        Place(SendCard, 0, 0, 1);
        if (wide) { Place(TransferCard, 0, 1, 2); Place(SpacesCard, 1, 0, 1); Place(CrossCard, 2, 0, 1); Place(StorageCard, 2, 1, 1); }
        else { Place(TransferCard, 1, 0, 1); Place(SpacesCard, 2, 0, 1); }
        CrossCard.Visibility = StorageCard.Visibility = Spark.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        OrbHost.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        SpaceSubtitle.Visibility = height < 600 ? Visibility.Collapsed : Visibility.Visible;
        SelectionLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        // Thanh trên và tiêu đề thẻ tệp thu gọn dần để nút cửa sổ và tên không gian không bao giờ bị đẩy ra ngoài.
        var filesWidth = width - 148 - SideGrid.Width;
        MembersText.Visibility = CopyLinkText.Visibility = filesWidth < 520 ? Visibility.Collapsed : Visibility.Visible;
        BrandText.Visibility = width < 1100 ? Visibility.Collapsed : Visibility.Visible;
        AccountMenuText.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        AccountMenuText.MaxWidth = wide ? 220 : 150;
        SearchHost.Width = wide ? 290 : narrow ? 150 : 220;
    }
    private void Window_SizeChanged(object s, SizeChangedEventArgs e) { if (IsLoaded) ApplyZoom(); }
    private void ChangeZoom(double? step)
    {
        var previous = state.Settings.UiScale;
        state.Settings.UiScale = step is null ? 0 : Math.Clamp(Math.Round((UiScaleTransform.ScaleX + step.Value) * 100) / 100, .6, 1.5);
        try { store.Save(state); ApplyZoom(); }
        catch (Exception ex) { state.Settings.UiScale = previous; ShowError(ex); }
    }
    private void ZoomOut_Click(object s, RoutedEventArgs e) => ChangeZoom(-.05);
    private void ZoomIn_Click(object s, RoutedEventArgs e) => ChangeZoom(.05);
    private void ZoomReset_Click(object s, RoutedEventArgs e) => ChangeZoom(null);
    private void Zoom_MouseWheel(object s, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        ChangeZoom(e.Delta > 0 ? .05 : -.05); e.Handled = true;
    }
    private void Zoom_KeyDown(object s, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key is Key.Add or Key.OemPlus) ChangeZoom(.05);
        else if (e.Key is Key.Subtract or Key.OemMinus) ChangeZoom(-.05);
        else if (e.Key is Key.D0 or Key.NumPad0) ChangeZoom(null);
        else return;
        e.Handled = true;
    }
    private async void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (applying || !IsLoaded || demo) return;
        UpdateAccountMenu();
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
        CancelUploads.IsEnabled = transfers.Uploads.Count > 0;
        CancelDownloads.IsEnabled = transfers.Downloads.Count > 0;
        CrossButton.IsEnabled = CanCross();
        var active = transfers.Uploads.Count + transfers.Downloads.Count;
        LiveText.Text = active > 0 ? $"Đang chạy · {active}" : "Rảnh";
        LiveText.Foreground = (Brush)FindResource(active > 0 ? "GlGreen" : "GlMuted");
        LiveDot.Fill = (Brush)FindResource(active > 0 ? "GlGreen" : "GlFaint");
        LiveDot.BeginAnimation(OpacityProperty, active > 0 && motion ? new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever } : null);
    }
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (installingUpdate) return;
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
        if (ex is ReauthRequiredException reauth) { OfferReauth(reauth.Account, userAction: true); return; }
        StatusLabel.Text = "Thao tác chưa hoàn tất. " + ex.Message;
        if (!lifetime.IsCancellationRequested) MessageBox.Show(this, ex.Message, "DriveLink", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private bool Ready(bool space = true)
    {
        if (installingUpdate) return false;
        if (demo) { MessageBox.Show(this, "Đây là dữ liệu minh họa. Bấm Thoát xem thử rồi kết nối Google để sử dụng thật.", "Xem thử"); return false; }
        if (CurrentAccount is null) { MessageBox.Show(this, "Thêm tài khoản Google trước khi tiếp tục.", "Chưa kết nối"); return false; }
        if (space && CurrentSpace is null) { MessageBox.Show(this, "Tạo hoặc chọn một không gian chung trước.", "Chưa có không gian"); return false; }
        return true;
    }
    private async Task RefreshAsync(bool background = false)
    {
        if (installingUpdate || busy || refreshing || demo || lifetime.IsCancellationRequested) return;
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
                spaceCache[account.Id] = (DateTimeOffset.UtcNow, spaces.ToList());
            }
            if (lastSpaceRefresh >= DateTimeOffset.UtcNow.AddSeconds(-2)) _ = RefreshQuotaAsync(account);
            RenderSpaceHeader();
            if (CurrentSpace is { } space)
            {
                SpaceTitle.Text = space.ToString();
                SpaceSubtitle.Text = "Thả file hoặc thư mục vào bất kỳ đâu · Tự làm mới mỗi 15 giây";
                var result = await drive.ListFilesAsync(account, space.Id, lifetime.Token);
                files = result.OrderByDescending(x => x.CreatedAt).ToList();
                RenderUpdatedStatus(account.Email);
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
        var selectedId = CurrentFile?.Id ?? keepSelectionId;
        if (files.Count > 0) keepSelectionId = null; // Chỉ giữ lựa chọn qua đúng một lần tải lại danh sách.
        IEnumerable<DriveItem> visible = files;
        if (filter == 1) visible = visible.Where(x => x.Owners.Any(o => o.Me));
        if (filter == 2) visible = visible.Where(x => !x.Owners.Any(o => o.Me));
        var search = SearchBox.Text.Trim();
        if (search.Length > 0) visible = visible.Where(x => x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || x.OwnerEmail.Contains(search, StringComparison.OrdinalIgnoreCase));
        var rows = visible.ToList();
        FilesGrid.ItemsSource = rows;
        FilesGrid.SelectedItem = rows.FirstOrDefault(x => x.Id == selectedId);
        CountLabel.Text = $"{rows.Count} mục";
        EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (search.Length > 0 || filter != 0) { EmptyTitle.Text = "Chưa có file phù hợp"; EmptyDescription.Text = "Thử từ khóa khác hoặc quay về File gần đây."; }
        else { EmptyTitle.Text = "Sẵn sàng cho file đầu tiên"; EmptyDescription.Text = CurrentSpace is null ? "Thêm tài khoản Google, tạo không gian và mời tài khoản trên máy còn lại để bắt đầu." : "Thả file vào bất kỳ đâu trong ứng dụng để gửi vào không gian đang chọn."; }
    }
    private void RenderUpdatedStatus(string email)
    {
        StatusLabel.Inlines.Clear(); StatusLabel.Inlines.Add(new Run("Đã cập nhật lúc "));
        StatusLabel.Inlines.Add(new Run(DateTime.Now.ToString("HH:mm:ss")) { Foreground = (Brush)FindResource("GlAccent"), FontWeight = FontWeights.SemiBold });
        StatusLabel.Inlines.Add(new Run(" | " + DisplayPrivacy.MaskEmail(email)));
    }
    private void SetFilter(int value, string title)
    {
        filter = value; ListTitle.Text = title;
        new[] { RecentButton, SentButton, ReceivedButton }[value].IsChecked = true;
        DockShift.BeginAnimation(TranslateTransform.YProperty, motion ? Spring(value * 52, 560) : null);
        if (!motion) DockShift.Y = value * 52;
        RenderFiles();
    }
    private void Recent_Click(object s, RoutedEventArgs e) => SetFilter(0, "File gần đây");
    private void Sent_Click(object s, RoutedEventArgs e) => SetFilter(1, "Tôi đã gửi");
    private void Received_Click(object s, RoutedEventArgs e) => SetFilter(2, "Từ người khác");
    private void Search_Changed(object s, TextChangedEventArgs e) { if (IsLoaded) RenderFiles(); }
    private void File_Changed(object s, SelectionChangedEventArgs e)
    {
        SelectionLabel.Text = CurrentFile is { } file ? file.Name : "Chọn file để tải về hoặc mở trên Drive.";
        UpdateCrossPanel();
    }
    private async void Refresh_Click(object s, RoutedEventArgs e) => await RefreshAsync();
    private void Cancel_Click(object s, RoutedEventArgs e) => operation?.Cancel();
    private Services.Lan.LanService EnsureLan()
    {
        if (lan is not null) return lan;
        var folder = store.Folder;
        lanTrust = new Services.Lan.LanTrustStore(folder);
        lan = new Services.Lan.LanService(Services.Lan.LanIdentity.Load(folder), lanTrust)
        {
            ReceiveFolder = () => state.Settings.ResolveInboxFolder(),
            ConfirmPairing = (name, code) => Dispatcher.InvokeAsync(() =>
            {
                ShowFromTray();
                return MessageBox.Show(this, $"Máy “{name}” trong mạng LAN muốn ghép nối với máy này.\n\nMã trên máy này: {code}\n\nChỉ bấm Có nếu bạn vừa bấm Ghép nối trên máy kia VÀ mã hiện ở đó giống hệt.", "Ghép nối LAN", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            }).Task,
        };
        // Nhận cả lô: ghi nhật ký và báo MỘT lần cho cả lô, không mỗi tệp một thông báo.
        lan.BatchReceived += (from, paths) => Dispatcher.BeginInvoke(() =>
        {
            transfers.AddActivities(paths.Select(path => new ActivityEntry { Time = DateTimeOffset.Now, Direction = TransferDirection.Download, Name = Path.GetFileName(path), Space = "LAN ← " + from, Result = TransferState.Done, Bytes = File.Exists(path) ? new FileInfo(path).Length : 0, LocalPath = path, Note = "Nhận thẳng trong mạng LAN" }).ToList());
            var title = "Nhận thẳng qua LAN từ " + from;
            var text = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} tệp · {Path.GetFileName(paths[^1])}…";
            if (IsVisible && IsActive) ShowIsland(IslandKind.Done, title, text, 460, 4.5);
            else tray.Notify(title, text + "\nĐã lưu vào Hộp thư đến\\LAN", () => Dispatcher.Invoke(ShowFromTray));
        });
        return lan;
    }
    private string? StartLan()
    {
        try { EnsureLan().Start(); return null; }
        catch (System.Net.Sockets.SocketException ex) { _ = lan?.StopAsync(); return "Không mở được cổng LAN (có thể app khác đang dùng cổng 47210/47211): " + ex.Message; }
    }
    private LanWindow? lanWindow;
    private void Lan_Click(object sender, RoutedEventArgs e)
    {
        if (preview || demo) return;
        if (lanWindow is { IsLoaded: true }) { lanWindow.Activate(); return; }
        var service = EnsureLan();
        lanWindow = new LanWindow(service, lanTrust!, service.IsRunning, on =>
        {
            string? error = null;
            if (on) error = StartLan(); else _ = service.StopAsync();
            state.Settings.LanEnabled = on && error is null;
            try { store.Save(state); } catch (Exception ex) { error ??= ex.Message; }
            return error;
        }, entries => transfers.AddActivities(entries.Select(e => new ActivityEntry { Time = DateTimeOffset.Now, Direction = TransferDirection.Upload, Name = e.Name, Space = "LAN", Result = e.Ok ? TransferState.Done : TransferState.Failed, Bytes = e.Bytes, Seconds = e.Elapsed.TotalSeconds, Note = e.Note }).ToList())) { Owner = this };
        lanWindow.Closed += (_, _) => lanWindow = null;
        lanWindow.Show();
    }
    private QueueWindow? queueWindow;
    private void Queue_Click(object sender, RoutedEventArgs e)
    {
        if (queueWindow is { IsLoaded: true }) { queueWindow.Activate(); return; }
        queueWindow = new QueueWindow(transfers) { Owner = this };
        queueWindow.Closed += (_, _) => queueWindow = null;
        queueWindow.Show();
    }
    private void CancelUploads_Click(object s, RoutedEventArgs e) => transfers.CancelAll(TransferDirection.Upload);
    private void CancelDownloads_Click(object s, RoutedEventArgs e) => transfers.CancelAll(TransferDirection.Download);

    private void Settings_Click(object s, RoutedEventArgs e)
    {
        if (busy || refreshing || HasTransfers) { StatusLabel.Text = "Chờ truyền file hoàn tất trước khi thay đổi tài khoản."; return; }
        poll.Stop();
        var dialog = new SettingsWindow(state, store, OpenUpdates, account => auth.RevokeAsync(account, lifetime.Token)) { Owner = this };
        dialog.ShowDialog();
        ApplyUserSettings();
        poll.Start();
        if (demo) return;
        // Nhớ tệp đang chọn trước khi làm mới; xóa danh sách không gian ở chế độ "applying" để không bị coi là người dùng đổi không gian.
        keepSelectionId = CurrentFile?.Id;
        ReloadAccounts();
        applying = true; SpacesList.ItemsSource = null; applying = false;
        ClearFilesKeepingSelection();
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
        await ConnectCoreAsync(null);
    }
    private readonly HashSet<string> reauthOffered = [];
    /// <summary>
    /// Quyền Google của một tài khoản đã hết hạn: hỏi đăng nhập lại ngay (một chạm), hoặc báo bằng thông báo Windows khi app đang ẩn.
    /// Mỗi tài khoản chỉ tự hỏi một lần mỗi lần mở app; menu tài khoản luôn có mục Đăng nhập lại.
    /// </summary>
    private void OfferReauth(Account account, bool userAction = false)
    {
        if (lifetime.IsCancellationRequested || demo) return;
        if (CurrentAccount?.Id == account.Id) UpdateAccountMenu();
        StatusLabel.Text = $"Phiên Google của {DisplayPrivacy.MaskEmail(account.Email)} đã hết hạn · mở menu tài khoản → Đăng nhập lại.";
        if (!userAction && !reauthOffered.Add(account.Id)) return;
        var text = $"Google báo phiên đăng nhập của {DisplayPrivacy.MaskEmail(account.Email)} đã hết hạn hoặc bị thu hồi.";
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            tray.Notify("Cần đăng nhập lại Google", text + " Bấm để đăng nhập lại.", () => Dispatcher.Invoke(async () => { ShowFromTray(); await ConnectCoreAsync(account); }));
            return;
        }
        if (busy || HasTransfers) return; // Đang bận: đã báo ở dòng trạng thái và menu tài khoản.
        if (MessageBox.Show(this, text + "\n\nĐăng nhập lại ngay? Trình duyệt sẽ mở với sẵn email này; tài khoản, không gian và cài đặt được giữ nguyên.",
                "Cần đăng nhập lại Google", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _ = ConnectCoreAsync(account);
    }
    /// <summary>Thêm tài khoản mới, hoặc đăng nhập lại đúng tài khoản <paramref name="reconnect"/> (giữ nguyên tài khoản và dữ liệu đã lưu).</summary>
    private async Task ConnectCoreAsync(Account? reconnect)
    {
        if (busy || refreshing || HasTransfers) { StatusLabel.Text = "Chờ thao tác hiện tại hoàn tất trước khi đăng nhập."; return; }
        if (string.IsNullOrWhiteSpace(state.Config.ClientId)) { Settings_Click(this, new RoutedEventArgs()); if (string.IsNullOrWhiteSpace(state.Config.ClientId)) return; }
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetBusy(true);
        var login = new LoginStatusWindow(() => operation?.Cancel()) { Owner = this };
        login.Show();
        try
        {
            StatusLabel.Text = reconnect is null ? "Đang chờ bạn đăng nhập Google trong trình duyệt…" : "Đang chờ bạn đăng nhập lại " + DisplayPrivacy.MaskEmail(reconnect.Email) + " trong trình duyệt…";
            var account = await auth.ConnectAsync(operation.Token, reconnect);
            reauthOffered.Remove(account.Id);
            if (reconnect is not null) SecurityLog.Write("token-renewed", "Đăng nhập lại " + DisplayPrivacy.MaskEmail(account.Email));
            ReloadAccounts();
            StatusLabel.Text = "Đã kết nối Google: " + DisplayPrivacy.MaskEmail(account.Email);
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
        var menu = new ContextMenu { LayoutTransform = new ScaleTransform(UiPresentation.Scale, UiPresentation.Scale) };
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
        var e2e = new MenuItem { Header = e2eKeys.Find(space.Id) is null ? "Mã hóa đầu cuối…" : "Mã hóa đầu cuối (đang bật)…" };
        e2e.Click += (_, _) => { if (new E2eWindow(e2eKeys, space.Id, space.ToString()) { Owner = this }.ShowDialog() == true) RenderSpaceHeader(); };
        menu.Items.Add(e2e);
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
        var dialog = new FileShareWindow(drive, CurrentAccount!, new DriveItem { Id = CurrentSpace!.Id, Name = CurrentSpace.DisplayName, MimeType = "application/vnd.google-apps.folder" }) { Owner = this };
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
        var picker = new OpenFolderDialog { Title = state.Settings.FolderUploadMode == "tree" ? "Chọn thư mục để gửi nguyên cấu trúc" : "Chọn thư mục để nén rồi gửi", Multiselect = true };
        if (picker.ShowDialog(this) == true) await UploadPathsAsync(picker.FolderNames);
    }
    private void Upload_DragOver(object s, DragEventArgs e)
    {
        var canSend = !busy && !demo && CurrentAccount is not null && CurrentSpace is not null && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = canSend ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = canSend && state.Settings.ShowDropOverlay ? Visibility.Visible : Visibility.Collapsed;
        if (canSend && CurrentSpace is { } space) DropOverlayHint.Text = "Vào “" + space + "” · Bạn có thể tiếp tục gửi trong khi đang nhận file.";
        SetDropState(canSend);
        e.Handled = true;
    }
    private void Upload_DragLeave(object s, DragEventArgs e)
    {
        var point = e.GetPosition(this);
        if (point.X <= 0 || point.Y <= 0 || point.X >= ActualWidth || point.Y >= ActualHeight)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            SetDropState(false);
        }
    }
    private async void Upload_Drop(object s, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        SetDropState(false);
        e.Handled = true;
        if (!Ready() || busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await UploadPathsAsync(paths);
    }
    private Task UploadPathsAsync(string[] paths)
    {
        if (!Ready() || busy || lifetime.IsCancellationRequested) return Task.CompletedTask;
        var account = CurrentAccount!; var space = CurrentSpace!;
        if (state.Settings.FolderUploadMode == "tree" && paths.Any(Directory.Exists))
        {
            _ = UploadTreesAsync(account, space, paths);
            return Task.CompletedTask;
        }
        foreach (var path in paths) transfers.EnqueueUpload(account, space, path);
        UploadLabel.Text = paths.Length == 1 ? "Đang chờ gửi: " + Path.GetFileName(paths[0]) : $"Đã thêm {paths.Length} mục vào hàng gửi";
        return Task.CompletedTask;
    }
    /// <summary>Gửi nguyên cây thư mục: tạo các thư mục con trên Drive rồi đưa từng tệp vào hàng gửi song song.</summary>
    private async Task UploadTreesAsync(Account account, Space space, string[] paths)
    {
        try
        {
            foreach (var path in paths)
            {
                if (!Directory.Exists(path)) { transfers.EnqueueUpload(account, space, path); continue; }
                UploadLabel.Text = "Đang tạo cấu trúc thư mục: " + Path.GetFileName(path);
                var count = await transfers.EnqueueTreeAsync(account, space, path, lifetime.Token);
                UploadLabel.Text = $"Đã thêm {count} tệp của “{Path.GetFileName(path)}” vào hàng gửi";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
    }
    /// <summary>Thẻ Truyền tải hiển thị tổng của mọi tệp đang chạy song song ở từng chiều.</summary>
    private void RenderTransfers()
    {
        if (lifetime.IsCancellationRequested) return;
        Render(TransferDirection.Upload, UploadLabel, UploadProgress, UploadSpeed, "gửi", ref lastUploadBps);
        Render(TransferDirection.Download, DownloadLabel, DownloadProgress, DownloadSpeed, "nhận", ref lastDownloadBps);
        void Render(TransferDirection direction, TextBlock label, ProgressBar bar, TextBlock speed, string verb, ref double bps)
        {
            var (active, waiting, bytes, total, rate, retrying) = transfers.Summary(direction);
            bps = rate; speed.Text = DriveItem.FormatSize((long)rate) + "/s";
            if (active == 0 && waiting == 0) return;
            var running = transfers.Items.Where(i => i.Direction == direction && i.IsActive).ToList();
            var preparing = running.FirstOrDefault(i => i.State == TransferState.Preparing);
            bar.IsIndeterminate = preparing is not null && total == 0;
            bar.Value = total > 0 ? Math.Clamp(bytes * 100d / total, 0, 100) : 0;
            var head = active == 1 && running.FirstOrDefault(i => i.State is TransferState.Running or TransferState.Retrying or TransferState.Preparing) is { } one
                ? one.Name + (one.Note.Length > 0 && one.State != TransferState.Running ? " · " + one.Note : "")
                : $"Đang {verb} {active} tệp song song";
            label.Text = (retrying ? "Mạng chập chờn, đang thử lại · " : "") + head + (total > 0 ? $" · {bar.Value:0}%" : "") + (waiting > 0 ? $" · {waiting} chờ" : "");
        }
    }
    private readonly DispatcherTimer refreshAfterUpload = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private void OnTransferFinished(TransferItem item)
    {
        if (lifetime.IsCancellationRequested) return;
        var upload = item.Direction == TransferDirection.Upload;
        if (!upload) downloadTargets.Remove(item.LocalPath);
        var label = upload ? UploadLabel : DownloadLabel;
        switch (item.State)
        {
            case TransferState.Done:
                if (item.FileId is { Length: > 0 } id)
                {
                    RememberLocalFile(id, item.LocalPath);
                    if (!upload && downloadAccounts.Remove(item.Id, out var owner)) _ = MarkReceivedAsync(owner, id);
                }
                label.Text = (upload ? "Đã gửi: " : "Đã nhận: ") + item.Name + (item.Note.Length > 0 ? " · " + item.Note : "");
                ShowIsland(IslandKind.Done, (upload ? "Đã gửi " : "Đã nhận ") + item.Name, upload ? "Vào “" + item.Space + "”" : "Nhấn vào tệp trong danh sách để mở thư mục trên máy", 440, 3.5);
                if (upload) { refreshAfterUpload.Stop(); refreshAfterUpload.Start(); }
                else StatusLabel.Text = "Đã tải xong. Nhấn vào file trong danh sách để mở thư mục trên máy.";
                break;
            case TransferState.Canceled:
                label.Text = (upload ? "Đã hủy gửi: " : "Đã hủy nhận: ") + item.Name;
                break;
            case TransferState.Failed:
                label.Text = (upload ? "Gửi chưa hoàn tất: " : "Nhận chưa hoàn tất: ") + item.Name;
                ShowIsland(IslandKind.Warn, (upload ? "Chưa gửi được " : "Chưa nhận được ") + item.Name, item.Note.Split('\n')[0], 460, 6);
                StatusLabel.Text = item.Note;
                break;
        }
        RenderTransfers(); UpdateControls();
    }
    /// <summary>Ghi "máy này đã nhận" lên tệp để người gửi thấy. Không có quyền sửa thì bỏ qua.</summary>
    private async Task MarkReceivedAsync(Account account, string fileId)
    {
        try { await drive.MarkReceivedAsync(account, fileId, lifetime.Token); }
        catch (Exception) { /* Chỉ là thông tin thêm; lỗi mạng không ảnh hưởng tệp đã nhận. */ }
    }
    /// <summary>Kiểm tra tệp mới của mọi tài khoản (vài giây một lần, rất nhẹ).</summary>
    private async Task WatchAsync()
    {
        if (watching || demo || installingUpdate || lifetime.IsCancellationRequested) return;
        watching = true;
        try
        {
            foreach (var account in state.Accounts.ToList())
            {
                if (!spaceCache.TryGetValue(account.Id, out var cached) || DateTimeOffset.UtcNow - cached.At > TimeSpan.FromMinutes(10))
                {
                    try { spaceCache[account.Id] = (DateTimeOffset.UtcNow, await drive.ListSpacesAsync(account, lifetime.Token)); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
                }
                try { await inbox.CheckAsync(account, lifetime.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* Mạng hoặc Windows Hello chưa mở khóa: lần sau thử lại. */ }
            }
        }
        catch (OperationCanceledException) { }
        finally { watching = false; }
    }
    private void OnIncoming(IncomingFile file)
    {
        var settings = state.Settings;
        var risky = RiskyFiles.IsExecutable(file.File.Name);
        var auto = settings.AutoDownloadInbox && !risky && file.File.Capabilities.CanDownload && (!file.File.IsEncrypted || e2eKeys.KeyFor(file.File.E2eSpace) is not null) && (settings.AutoDownloadMaxMb <= 0 || file.File.Size <= settings.AutoDownloadMaxMb * 1048576L);
        if (auto)
        {
            try
            {
                var folder = Path.Combine(settings.ResolveInboxFolder(), string.Concat(file.Space.ToString().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));
                Directory.CreateDirectory(folder);
                var target = InboxWatcher.UniquePath(folder, file.File.PlainName);
                if (downloadTargets.Add(target)) downloadAccounts[transfers.EnqueueDownload(file.Account, file.File, target, file.Space.ToString()).Id] = file.Account;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { auto = false; StatusLabel.Text = "Không tạo được thư mục Hộp thư đến: " + ex.Message; }
        }
        if (CurrentAccount?.Id == file.Account.Id && CurrentSpace?.Id == file.Space.Id) { refreshAfterUpload.Stop(); refreshAfterUpload.Start(); }
        incoming.Add((file, auto, risky));
        notifyTimer.Stop(); notifyTimer.Start();
    }
    /// <summary>Gom các tệp đến cùng lúc thành một thông báo.</summary>
    private void FlushIncoming()
    {
        if (incoming.Count == 0) return;
        var batch = incoming.ToList(); incoming.Clear();
        string title, text;
        if (batch.Count == 1)
        {
            var (file, auto, risky) = batch[0];
            var from = file.File.SenderMachine.Length > 0 ? "máy " + file.File.SenderMachine : file.File.OwnerLabel;
            title = "Tệp mới trong “" + file.Space + "”";
            text = $"{file.File.Name} · {file.File.SizeLabel} · từ {from}" + (auto ? "\nĐang tải về Hộp thư đến" : risky ? "\nTệp chạy được: không tự tải, hãy kiểm tra người gửi" : "");
        }
        else
        {
            title = $"{batch.Count} tệp mới";
            text = string.Join(", ", batch.Take(3).Select(x => x.File.File.Name)) + (batch.Count > 3 ? $" và {batch.Count - 3} tệp khác" : "") + (batch.Any(x => x.Auto) ? "\nĐang tải về Hộp thư đến" : "");
        }
        if (IsVisible && IsActive) ShowIsland(IslandKind.Done, title, text.Split('\n')[0], 460, 4.5);
        else if (state.Settings.NotifyNewFiles) tray.Notify(title, text, () => Dispatcher.Invoke(ShowFromTray));
    }
    /// <summary>Lượt gửi/nhận bị dừng do tắt app lần trước: hỏi để tiếp tục từ chỗ đã dừng.</summary>
    private void OfferResumeTransfers()
    {
        var pending = transfers.Unfinished();
        if (pending.Count == 0) return;
        var uploadsLeft = pending.Count(r => r.Direction == TransferDirection.Upload);
        var answer = MessageBox.Show(this, $"Lần trước còn {pending.Count} lượt chưa xong ({uploadsLeft} gửi, {pending.Count - uploadsLeft} nhận). Tiếp tục từ chỗ đã dừng?\n\nChọn Không để bỏ các lượt này (phần đã tải dở sẽ bị xóa).", "Tiếp tục truyền tệp", MessageBoxButton.YesNo, MessageBoxImage.Question);
        foreach (var record in pending)
        {
            var account = state.Accounts.FirstOrDefault(a => a.Id == record.AccountId);
            var valid = answer == MessageBoxResult.Yes && account is not null && (record.Direction == TransferDirection.Download ? Directory.Exists(Path.GetDirectoryName(record.Destination)) : File.Exists(record.SourcePath) || Directory.Exists(record.SourcePath));
            if (!valid) { transfers.Discard(record); continue; }
            if (record.Direction == TransferDirection.Upload) transfers.EnqueueUpload(account!, new Space { Id = record.SpaceId, Name = record.SpaceName }, record.SourcePath, record);
            else if (downloadTargets.Add(record.Destination)) transfers.EnqueueDownload(account!, TransferCenter.ToDriveItem(record), record.Destination, record.SpaceName, record);
        }
    }
    private async void Download_Click(object s, RoutedEventArgs e)
    {
        if (!Ready() || busy || lifetime.IsCancellationRequested || !EnsureFileSelected("tải về") || CurrentFile is not { } file) return;
        if (SelectedFiles is { Count: > 1 } many) { DownloadMany(many); return; }
        if (file.MimeType.StartsWith("application/vnd.google-apps.")) { MessageBox.Show(this, "Dùng Mở trên Drive cho thư mục và tài liệu Google. Bản đầu chưa hỗ trợ xuất tài liệu Google.", "Tải về"); return; }
        if (!ConfirmRiskyFile(file, "tải về")) return;
        if (!EnsureE2eKey(file)) return;
        var safeName = string.Concat(file.PlainName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var picker = new SaveFileDialog { FileName = safeName, Title = "Lưu file", OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        var target = Path.GetFullPath(picker.FileName);
        if (!downloadTargets.Add(target)) { StatusLabel.Text = "File đích này đang được tải hoặc đã nằm trong hàng đợi."; return; }
        var manual = transfers.EnqueueDownload(CurrentAccount!, file, target, CurrentSpace?.ToString() ?? "");
        downloadAccounts[manual.Id] = CurrentAccount!;
        DownloadLabel.Text = "Đang chờ nhận: " + file.Name;
    }
    /// <summary>Tệp mã hóa đầu cuối cần khóa của không gian: hỏi mật khẩu nếu máy này chưa có. Tệp thường thì luôn được.</summary>
    private bool EnsureE2eKey(DriveItem file)
    {
        if (!file.IsEncrypted || e2eKeys.KeyFor(file.E2eSpace) is not null) return true;
        var name = spaceCache.Values.SelectMany(c => c.Spaces).FirstOrDefault(s => s.Id == file.E2eSpace)?.ToString() ?? CurrentSpace?.ToString() ?? "không gian của tệp";
        return new E2eWindow(e2eKeys, file.E2eSpace, name, askOnly: true) { Owner = this }.ShowDialog() == true && e2eKeys.KeyFor(file.E2eSpace) is not null;
    }
    /// <summary>Tải nhiều tệp cùng lúc vào một thư mục (chạy song song theo hàng nhận). Thư mục và tài liệu Google được bỏ qua.</summary>
    private void DownloadMany(List<DriveItem> items)
    {
        var downloadable = items.Where(x => !x.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal)).ToList();
        if (downloadable.Count == 0) { MessageBox.Show(this, "Các mục đã chọn là thư mục hoặc tài liệu Google. Dùng Mở trên Drive cho các mục này.", "Tải về"); return; }
        if (downloadable.Any(x => !ConfirmRiskyFile(x, "tải về") || !EnsureE2eKey(x))) return;
        var picker = new OpenFolderDialog { Title = $"Chọn thư mục lưu {downloadable.Count} tệp" };
        if (picker.ShowDialog(this) != true) return;
        foreach (var item in downloadable)
        {
            var target = InboxWatcher.UniquePath(picker.FolderName, item.PlainName);
            if (!downloadTargets.Add(target)) continue;
            downloadAccounts[transfers.EnqueueDownload(CurrentAccount!, item, target, CurrentSpace?.ToString() ?? "").Id] = CurrentAccount!;
        }
        DownloadLabel.Text = $"Đã thêm {downloadable.Count} tệp vào hàng nhận" + (downloadable.Count < items.Count ? $" (bỏ qua {items.Count - downloadable.Count} thư mục/tài liệu Google)" : "");
    }
    private void OpenFile_Click(object s, RoutedEventArgs e)
    {
        if (!Ready() || !EnsureFileSelected("mở trên Drive") || CurrentFile is not { } file) return;
        try { Process.Start(new ProcessStartInfo(file.BrowserUrl) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private readonly HashSet<string> riskyConfirmed = [];
    /// <summary>
    /// Tệp chạy được (.exe, .bat, .ps1, .lnk…) do người khác gửi có thể chứa mã độc. Hỏi trước khi tải hoặc mở thư mục chứa nó;
    /// mỗi tệp chỉ hỏi một lần trong một lần mở app. Tệp do chính tài khoản này gửi thì không hỏi.
    /// </summary>
    private bool ConfirmRiskyFile(DriveItem item, string action)
    {
        if (!RiskyFiles.IsExecutable(item.Name) || item.IsMine || riskyConfirmed.Contains(item.Id)) return true;
        var sender = item.OwnerEmail.Length > 0 ? DisplayPrivacy.MaskEmail(item.OwnerEmail) : "người khác";
        var answer = MessageBox.Show(this,
            $"“{item.Name}” là tệp có thể chạy chương trình trên máy, do {sender} gửi vào không gian chung.\n\n" +
            "Tệp loại này có thể chứa mã độc. Chỉ tiếp tục nếu bạn biết rõ người gửi và đang chờ đúng tệp này. Khi đã tải về, đừng mở nếu Windows hoặc phần mềm diệt virus cảnh báo.\n\n" +
            $"Vẫn {action}?", "Tệp có thể chạy chương trình", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        SecurityLog.Write(answer == MessageBoxResult.Yes ? "risky-file-accepted" : "risky-file-declined", $"{(answer == MessageBoxResult.Yes ? "Đồng ý" : "Từ chối")} {action} tệp chạy được {Path.GetExtension(item.Name)} từ {sender} · tệp {item.Id}");
        if (answer != MessageBoxResult.Yes) { StatusLabel.Text = "Đã dừng: tệp chạy được từ " + sender + "."; return false; }
        riskyConfirmed.Add(item.Id);
        return true;
    }
    private string? keepSelectionId;
    /// <summary>Xóa danh sách để tải lại nhưng nhớ tệp đang chọn, để chọn lại khi danh sách mới về.</summary>
    private void ClearFilesKeepingSelection()
    {
        keepSelectionId = CurrentFile?.Id ?? keepSelectionId;
        files = []; RenderFiles();
    }
    /// <summary>Báo rõ khi bấm thao tác tệp mà chưa chọn tệp nào, thay vì im lặng.</summary>
    private bool EnsureFileSelected(string action)
    {
        if (CurrentFile is not null) return true;
        var message = files.Count == 0 ? "Danh sách chưa có tệp nào" : "Chọn một tệp trong danh sách để " + action;
        StatusLabel.Text = message + ".";
        ShowIsland(IslandKind.Warn, "Chưa chọn tệp", message, 380, 2.8);
        FilesCard.IsHighlighted = true;
        var unhighlight = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        unhighlight.Tick += (_, _) => { unhighlight.Stop(); FilesCard.IsHighlighted = false; };
        unhighlight.Start();
        if (files.Count > 0) FilesGrid.Focus();
        return false;
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
        row.ContextMenu = BuildFileMenu(item);
    }
    private ContextMenu BuildFileMenu(DriveItem item)
    {
        var menu = new ContextMenu { LayoutTransform = new ScaleTransform(UiPresentation.Scale, UiPresentation.Scale) };
        MenuItem Add(string label, RoutedEventHandler handler, bool enabled = true)
        {
            var entry = new MenuItem { Header = label, IsEnabled = enabled, CommandParameter = item }; entry.Click += handler; menu.Items.Add(entry); return entry;
        }
        var available = !demo && !busy && !refreshing && !installingUpdate;
        Add("Đổi tên", async (_, _) => await RenameFile(item), available && item.Capabilities.CanRename && !item.IsLocked);
        Add("Tải xuống", (_, e) => { FilesGrid.SelectedItem = item; Download_Click(this, e); }, available && item.Capabilities.CanDownload);
        Add("Tạo bản sao", async (_, _) => await CopyFile(item), available && item.Capabilities.CanCopy && item.MimeType != "application/vnd.google-apps.folder");
        var share = new MenuItem { Header = "Chia sẻ" };
        var shareDialog = new MenuItem { Header = "Chia sẻ…", IsEnabled = available };
        shareDialog.Click += (_, _) => ShareFile(item);
        var link = new MenuItem { Header = "Sao chép liên kết", IsEnabled = !demo, CommandParameter = item }; link.Click += CopyFileLink_Click;
        var cross = new MenuItem { Header = "Gửi sang tài khoản khác…", IsEnabled = available && !HasTransfers && item.MimeType != FolderMime && OtherAccounts.Any(), ToolTip = "Google sao chép trên máy chủ sang tài khoản Google khác đã thêm vào DriveLink" };
        cross.Click += (_, _) => { FilesGrid.SelectedItem = item; if (crossTarget is { } target) _ = RunCrossAsync(item, target); };
        share.Items.Add(shareDialog); share.Items.Add(link); share.Items.Add(cross); menu.Items.Add(share);
        var info = new MenuItem { Header = "Thông tin" };
        var details = new MenuItem { Header = "Thông tin chi tiết", IsEnabled = available }; details.Click += async (_, _) => await ShowFileDetails(item);
        var locked = new MenuItem { Header = item.IsLocked ? "Mở khóa" : "Khóa", IsEnabled = available && (item.IsLocked ? item.Capabilities.CanRemoveContentRestriction || item.Capabilities.CanModifyContentRestriction : item.Capabilities.CanModifyEditorContentRestriction || item.Capabilities.CanModifyContentRestriction) };
        locked.Click += async (_, _) => await ToggleFileLock(item);
        info.Items.Add(details); info.Items.Add(locked); menu.Items.Add(info);
        menu.Items.Add(new Separator());
        Add("Chuyển vào thùng rác", async (_, _) => await TrashFile(item), available && item.Capabilities.CanTrash);
        return menu;
    }
    private async Task RenameFile(DriveItem item)
    {
        if (!Ready() || CurrentAccount is not { } account) return;
        var prompt = new PromptWindow("Đổi tên", "Nhập tên mới cho file trên Google Drive.", "Tên file", "Lưu", item.Name, 255) { Owner = this };
        if (prompt.ShowDialog() != true || prompt.Value == item.Name) return;
        await RunAsync(async ct => { await drive.RenameAsync(account, item.Id, prompt.Value, ct); StatusLabel.Text = "Đã đổi tên file."; });
        await RefreshAsync();
    }
    private async Task CopyFile(DriveItem item)
    {
        if (!Ready() || CurrentAccount is not { } account || CurrentSpace is not { } space) return;
        await RunAsync(async ct => { await drive.CopyAsync(account, item.Id, "Bản sao của " + item.Name, space.Id, ct); StatusLabel.Text = "Đã tạo bản sao trong không gian hiện tại."; });
        await RefreshAsync();
    }
    private async Task TrashFile(DriveItem item)
    {
        if (!Ready() || CurrentAccount is not { } account) return;
        if (MessageBox.Show(this, "Chuyển “" + item.Name + "” vào Thùng rác Google Drive? Có thể khôi phục trên Drive.", "Chuyển vào thùng rác", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async ct => { await drive.TrashFileAsync(account, item.Id, ct); StatusLabel.Text = "Đã chuyển file vào Thùng rác Drive."; });
        await RefreshAsync();
    }
    private void ShareFile(DriveItem item)
    {
        if (!Ready() || busy || refreshing || CurrentAccount is not { } account) return;
        poll.Stop();
        try { new FileShareWindow(drive, account, item) { Owner = this }.ShowDialog(); }
        finally { poll.Start(); _ = RefreshAsync(); }
    }
    private async Task ShowFileDetails(DriveItem item)
    {
        if (!Ready() || CurrentAccount is not { } account) return;
        await RunAsync(async ct => { var fresh = await drive.GetFileAsync(account, item.Id, ct); new FileDetailsWindow(fresh) { Owner = this }.ShowDialog(); });
    }
    private async Task ToggleFileLock(DriveItem item)
    {
        if (!Ready() || CurrentAccount is not { } account) return;
        var action = item.IsLocked ? "Mở khóa" : "Khóa";
        if (MessageBox.Show(this, action + " chỉnh sửa cho “" + item.Name + "”?\nKhóa ngăn sửa nội dung, tên và nhận xét. Quyền xem/tải và chia sẻ không thay đổi. Người có quyền phù hợp có thể mở khóa lại.", action + " file", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async ct => { var fresh = await drive.GetFileAsync(account, item.Id, ct); if (fresh.IsLocked != item.IsLocked) throw new InvalidOperationException("Trạng thái khóa đã đổi trên Drive. Hãy làm mới danh sách."); await drive.SetLockedAsync(account, item.Id, !fresh.IsLocked, ct); StatusLabel.Text = "Đã " + action.ToLowerInvariant() + " file."; });
        await RefreshAsync();
    }
    private async void OpenLocalFolder(DriveItem item)
    {
        if (demo) { StatusLabel.Text = "Dữ liệu minh họa chưa có file trên máy."; return; }
        var path = history.FindKnownPath(item.Id);
        if (path is null) { StatusLabel.Text = "File chưa có trên máy hoặc đã di chuyển. Bấm Tải về để lưu bản cục bộ."; return; }
        if (folderOpening || !ConfirmRiskyFile(item, "mở thư mục chứa")) return;
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

    // ---------- Không gian và dung lượng ----------
    private void RenderSpaceHeader()
    {
        var space = CurrentSpace;
        SpaceDot.Fill = space is null ? (Brush)FindResource("GlFaint") : ToBrush(space.Color);
        SendTarget.Text = space is null ? "Chưa chọn không gian" : "→ " + space + (e2eKeys.Find(space.Id) is not null ? " · 🔐 mã hóa" : "");
    }
    private static Brush ToBrush(string color)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(color)!; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException) { return Brushes.CornflowerBlue; }
    }
    private async Task RefreshQuotaAsync(Account account)
    {
        try
        {
            var quota = await drive.GetStorageQuotaAsync(account, lifetime.Token);
            if (CurrentAccount?.Id == account.Id) RenderQuota(quota.Usage, quota.Limit, account);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (CurrentAccount?.Id != account.Id) return;
            QuotaPercent.Text = "—"; QuotaText.Text = "Chưa đọc được"; QuotaDetail.Text = "Sẽ thử lại khi làm mới";
        }
    }
    private void RenderQuota(long usage, long? limit, Account? account)
    {
        QuotaAccount.Text = account is null ? "" : DisplayPrivacy.MaskEmail(account.Email);
        if (limit is > 0)
        {
            var fraction = Math.Clamp((double)usage / limit.Value, 0, 1);
            QuotaPercent.Text = (fraction * 100).ToString("0", Vi) + "%";
            QuotaText.Text = $"{DriveItem.FormatSize(usage)} / {DriveItem.FormatSize(limit.Value)}";
            QuotaDetail.Text = "Còn trống " + DriveItem.FormatSize(Math.Max(0, limit.Value - usage));
            QuotaRing.Stroke = (Brush)FindResource(fraction > 0.9 ? "GlRose" : fraction > 0.75 ? "GlAmber" : "GlAccent");
            SetRing(QuotaRing, 78, fraction);
        }
        else
        {
            QuotaPercent.Text = "∞";
            QuotaText.Text = DriveItem.FormatSize(usage) + " đã dùng";
            QuotaDetail.Text = "Tài khoản không giới hạn dung lượng";
            SetRing(QuotaRing, 78, 0);
        }
    }
    private void SetRing(System.Windows.Shapes.Ellipse ring, double diameter, double fraction)
    {
        // Độ dài nét tính theo bội số độ dày nét: chu vi vòng chia độ dày.
        var length = Math.PI * (diameter - ring.StrokeThickness) / ring.StrokeThickness;
        ring.StrokeDashArray = [length, length];
        ring.Opacity = fraction > 0 ? 1 : 0;
        var target = length * (1 - fraction);
        if (!motion || preview) { ring.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null); ring.StrokeDashOffset = target; return; }
        ring.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, new DoubleAnimation(length, target, TimeSpan.FromMilliseconds(1100))
        { BeginTime = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---------- Gửi chéo tài khoản ----------
    private string? crossSummary, crossSummaryFileId;
    private IEnumerable<Account> OtherAccounts => state.Accounts.Where(x => x.Id != CurrentAccount?.Id);
    private bool CanCross() => !demo && !busy && !HasTransfers && !installingUpdate && !crossRunning
        && CurrentAccount is not null && crossTarget is not null && CurrentFile is not null;
    private void UpdateCrossPanel()
    {
        if (crossTarget is not null && (crossTarget.Id == CurrentAccount?.Id || !state.Accounts.Contains(crossTarget))) crossTarget = null;
        crossTarget ??= OtherAccounts.FirstOrDefault();
        CrossTargetText.Text = crossTarget is null ? "Thêm tài khoản thứ hai" : "Đến " + DisplayPrivacy.MaskEmail(crossTarget.Email);
        CrossTargetButton.ToolTip = crossTarget is null ? "Gửi chéo cần ít nhất hai tài khoản Google trong DriveLink" : "Chọn tài khoản đích";
        if (SelectedFiles is { Count: > 1 } many)
        {
            CrossSource.Text = $"{many.Count} mục đã chọn";
            CrossSourceMeta.Text = (many.Any(x => x.MimeType == FolderMime) ? "Có thư mục · giữ nguyên cấu trúc" : DriveItem.FormatSize(many.Sum(x => x.Size))) + " · chọn nơi nhận khi gửi";
        }
        else if (CurrentFile is { } file)
        {
            CrossSource.Text = file.Name;
            CrossSourceMeta.Text = (file.MimeType == FolderMime ? "Thư mục · giữ nguyên cấu trúc" : file.Size > 0 ? file.SizeLabel : "Tài liệu Google") + " · chọn nơi nhận khi gửi";
        }
        else
        {
            CrossSource.Text = "Chọn một tệp trong danh sách";
            CrossSourceMeta.Text = "Bản sao nằm trong Drive của tài khoản đích";
        }
        if (!crossRunning)
            CrossStatus.Text = crossTarget is null ? "Cần ít nhất hai tài khoản Google"
                : CurrentFile is null ? "Chưa chọn tệp"
                : CurrentFile.Id == crossSummaryFileId ? crossSummary : "Sẵn sàng";
        CrossButton.IsEnabled = CanCross();
    }
    private void CrossTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!OtherAccounts.Any()) { Connect_Click(sender, e); return; }
        var menu = new ContextMenu { LayoutTransform = new ScaleTransform(UiPresentation.Scale, UiPresentation.Scale) };
        foreach (var account in OtherAccounts)
        {
            var entry = new MenuItem { Header = DisplayPrivacy.MaskEmail(account.Email), IsCheckable = true, IsChecked = crossTarget?.Id == account.Id };
            entry.Click += (_, _) => { crossTarget = account; UpdateCrossPanel(); };
            menu.Items.Add(entry);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "＋ Thêm tài khoản Google" }; add.Click += Connect_Click; menu.Items.Add(add);
        CrossTargetButton.ContextMenu = menu; menu.PlacementTarget = CrossTargetButton; menu.IsOpen = true;
    }
    private async void Cross_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFile is null || crossTarget is not { } target || CurrentAccount is not { } source) return;
        var items = SelectedFiles;
        IReadOnlyList<Space> spaces = [];
        try { spaces = spaceCache.TryGetValue(target.Id, out var cached) ? cached.Spaces : await drive.ListSpacesAsync(target, lifetime.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusLabel.Text = "Chưa đọc được không gian của tài khoản đích; vẫn có thể gửi vào Drive của tôi."; }
        var summary = items.Count == 1 ? items[0].Name : $"{items.Count} mục ({items.Count(x => x.MimeType == FolderMime)} thư mục)";
        var picker = new CrossDestinationWindow(target.Email, summary, spaces, lastCrossFolder.GetValueOrDefault(target.Id)) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected is not { } destination) return;
        lastCrossFolder[target.Id] = destination.FolderId;
        if (items.Count == 1 && items[0].MimeType != FolderMime) await RunCrossAsync(items[0], target, destination.FolderId, destination.Label);
        else await RunCrossBatchAsync(source, items, target, destination);
    }
    /// <summary>Gửi chéo nhiều mục hoặc thư mục (giữ cấu trúc). Lỗi từng tệp được liệt kê, không dừng cả lượt.</summary>
    private async Task RunCrossBatchAsync(Account source, List<DriveItem> items, Account target, CrossDestinationWindow.Choice destination)
    {
        if (crossRunning || !Ready()) return;
        crossRunning = true; UpdateControls();
        CrossCard.IsHighlighted = true; StartFlow(true);
        var masked = DisplayPrivacy.MaskEmail(target.Email);
        ShowIsland(IslandKind.Busy, $"Đang gửi chéo {items.Count} mục", "Đến " + masked, 460);
        var phase = new Progress<string>(text => { CrossStatus.Text = text; IslandSub.Text = text; });
        CrossBatchResult? result = null;
        try
        {
            await RunAsync(async ct => result = await copier.CopyManyAsync(source, target, items, destination.FolderId, phase, size => Task.FromResult(MessageBox.Show(this,
                $"Google không cho cấp quyền xem tạm cho một số tệp. DriveLink có thể tải các tệp đó về máy này rồi tải lên tài khoản {masked} (tệp đầu tiên {DriveItem.FormatSize(size)}).\n\nTiếp tục cho mọi tệp như vậy trong lượt này?",
                "Gửi chéo qua máy", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes), null, ct));
        }
        finally { crossRunning = false; CrossCard.IsHighlighted = false; StartFlow(false); UpdateControls(); }
        if (result is not { } done) { HideIsland(); return; }
        var seconds = done.Elapsed.TotalSeconds.ToString("0.0", Vi) + "s";
        crossSummaryFileId = CurrentFile?.Id; crossSummary = $"{done.Files} tệp · {done.Folders} thư mục · {seconds}";
        var title = done.Failures.Count == 0 ? $"Đã gửi chéo {done.Files} tệp" : $"Đã gửi {done.Files} tệp · {done.Failures.Count} lỗi";
        ShowIsland(done.Failures.Count == 0 && !done.RevokeFailed ? IslandKind.Done : IslandKind.Warn, title, $"→ {masked} · {destination.Label} · {seconds}", 540, 6);
        if (done.Failures.Count > 0 || done.RevokeFailed)
            MessageBox.Show(this, (done.Failures.Count > 0 ? "Các mục chưa gửi được:\n• " + string.Join("\n• ", done.Failures.Take(15)) + (done.Failures.Count > 15 ? $"\n… và {done.Failures.Count - 15} mục khác" : "") : "")
                + (done.RevokeFailed ? $"\n\nChưa thu hồi được một quyền xem tạm của {masked}. DriveLink sẽ thử lại ở lần mở sau; bạn cũng có thể gỡ trong Chia sẻ." : ""),
                "Gửi chéo", MessageBoxButton.OK, MessageBoxImage.Warning);
        StatusLabel.Text = $"Đã gửi chéo {done.Files} tệp, {done.Folders} thư mục vào {destination.Label} của {masked}.";
        UpdateCrossPanel();
    }
    private async Task RunCrossAsync(DriveItem item, Account target, string folderId = "root", string destinationLabel = "Drive của tôi")
    {
        if (crossRunning || !Ready() || CurrentAccount is not { } source) return;
        for (var i = 0; i < 100 && refreshing; i++) await Task.Delay(100);
        crossRunning = true; UpdateControls();
        CrossCard.IsHighlighted = true; StartFlow(true);
        var masked = DisplayPrivacy.MaskEmail(target.Email);
        ShowIsland(IslandKind.Busy, "Đang gửi chéo · " + item.Name, "Đến " + masked, 460);
        var phase = new Progress<string>(text => { CrossStatus.Text = text; IslandSub.Text = text; });
        var relay = new Progress<TransferProgress>(value => { var text = $"Qua máy · {value.AmountLabel} · {value.SpeedLabel}"; CrossStatus.Text = text; IslandSub.Text = text; });
        CrossCopyResult? result = null;
        try
        {
            await RunAsync(async ct => result = await copier.CopyCoreAsync(source, target, item, folderId, phase, size => Task.FromResult(MessageBox.Show(this,
                $"Google không cho cấp quyền xem tạm cho tệp “{item.Name}” (có thể do chính sách chia sẻ của tổ chức).\n\n" +
                $"DriveLink có thể tải tệp về máy này rồi tải lên tài khoản {masked}. Việc này dùng khoảng {DriveItem.FormatSize(size * 2)} băng thông và cần {DriveItem.FormatSize(size)} chỗ trống tạm trên ổ đĩa.\n\nTiếp tục?",
                "Gửi chéo qua máy", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes), relay, ct));
        }
        finally { crossRunning = false; CrossCard.IsHighlighted = false; StartFlow(false); UpdateControls(); }
        crossSummaryFileId = item.Id;
        if (result is not { } done)
        {
            crossSummary = "Chưa hoàn tất";
            HideIsland(); UpdateCrossPanel(); return;
        }
        var through = done.BytesThroughMachine == 0 ? "0 MB qua máy này" : DriveItem.FormatSize(done.BytesThroughMachine) + " qua máy này";
        var seconds = done.Elapsed.TotalSeconds.ToString("0.0", Vi) + "s";
        crossSummary = $"Hoàn tất · {seconds} · {through}";
        StatusLabel.Text = $"Đã gửi chéo “{item.Name}” vào {destinationLabel} của {masked}.";
        if (done.RevokeFailed)
        {
            ShowIsland(IslandKind.Warn, "Đã sao chép · Chưa thu hồi được quyền tạm", "Mở Chia sẻ của tệp để gỡ quyền của " + masked, 520, 8);
            MessageBox.Show(this, $"Bản sao đã nằm trong Drive của {masked}, nhưng DriveLink chưa thu hồi được quyền xem tạm trên tệp gốc.\n\nHãy chuột phải tệp → Chia sẻ → Chia sẻ… và gỡ quyền của {masked}.",
                "Gửi chéo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            var title = done.Mode switch
            {
                CrossCopyMode.TemporaryShare => "Gửi chéo hoàn tất · Đã thu hồi quyền tạm",
                CrossCopyMode.Relay => "Gửi chéo hoàn tất · Đã tải qua máy",
                _ => "Gửi chéo hoàn tất",
            };
            ShowIsland(IslandKind.Done, title, $"{item.Name} → {masked} · {seconds} · {through}" + (done.Verified ? " · MD5 khớp" : ""), 540, 5);
        }
        UpdateCrossPanel();
    }
    private void Flow_SizeChanged(object sender, SizeChangedEventArgs e) => StartFlow(crossRunning);
    /// <summary>Các hạt sáng chạy dọc đường nối nguồn → đích; chỉ chạy khi đang gửi chéo để không tốn tài nguyên lúc rảnh.</summary>
    private void StartFlow(bool running)
    {
        FlowCanvas.Children.Clear();
        double width = FlowCanvas.ActualWidth, height = FlowCanvas.ActualHeight;
        if (width <= 0) return;
        const int count = 6;
        for (var i = 0; i < count; i++)
        {
            var shift = new TranslateTransform(running && motion ? -20 : width * (i + 0.5) / count, 0);
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = running ? 8 : 5, Height = running ? 8 : 5, Opacity = running ? 1 : 0.35, RenderTransform = shift,
                Fill = (Brush)FindResource(i % 2 == 0 ? "GlAccent" : "GlViolet"),
            };
            Canvas.SetTop(dot, (height - dot.Height) / 2);
            FlowCanvas.Children.Add(dot);
            if (!running || !motion) continue;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-10, width + 10, TimeSpan.FromSeconds(0.9))
            {
                BeginTime = TimeSpan.FromSeconds(0.9 * i / count), RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
    }

    // ---------- Biểu đồ tốc độ ----------
    private bool sparkFlat;
    private void Spark_SizeChanged(object sender, SizeChangedEventArgs e) { sparkFlat = false; DrawSpark(); }
    private void DrawSpark()
    {
        double width = Spark.ActualWidth, height = Spark.ActualHeight;
        if (width <= 0 || height <= 0 || Spark.Visibility != Visibility.Visible) return;
        var peak = Math.Max(upHistory.Max(), downHistory.Max());
        if (peak <= 0 && sparkFlat) return; // Không có dữ liệu mới: giữ nguyên hình, không vẽ lại.
        sparkFlat = peak <= 0;
        var max = Math.Max(1048576, peak) * 1.15;
        PointCollection Line(List<double> values)
        {
            var points = new PointCollection(values.Count);
            for (var i = 0; i < values.Count; i++) points.Add(new Point(i * width / (SparkPoints - 1), height - 3 - values[i] / max * (height - 8)));
            return points;
        }
        var up = Line(upHistory);
        SparkUp.Points = up;
        SparkDown.Points = Line(downHistory);
        SparkUpFill.Points = new PointCollection(up) { new Point(width, height), new Point(0, height) };
    }

    // ---------- Chuyển động ----------
    private void SetDropState(bool active)
    {
        if (dropActive == active) return;
        dropActive = active;
        SendCard.IsHighlighted = active;
        DropTitle.Text = active ? "Thả để gửi" : "Thả tệp vào bất kỳ đâu";
        var scale = active ? 1.15 : 1;
        if (!motion) { OrbScale.ScaleX = OrbScale.ScaleY = scale; return; }
        OrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, Spring(scale, 600));
        OrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, Spring(scale, 600));
    }
    private void PlayEntrance()
    {
        if (preview) return;
        GlassCard[] cards = [FilesCard, SendCard, TransferCard, SpacesCard, CrossCard, StorageCard];
        for (var i = 0; i < cards.Length; i++) cards[i].PlayEntrance(TimeSpan.FromMilliseconds(60 + i * 70), i == 0 ? 10 : 16);
    }
    /// <summary>Nền cực quang và vòng sóng chỉ chạy khi cửa sổ đang hiện và đang được dùng, ở 15 khung hình/giây, để gần như không tốn tài nguyên khi chạy nền.</summary>
    private void SetAmbient(bool on)
    {
        on &= IsActive || !IsLoaded;
        if (preview || !motion || on == ambientRunning) return;
        ambientRunning = on;
        var blobs = new[] { (BlobA, 90d, 60d, 19d), (BlobB, -100d, -50d, 23d), (BlobC, -70d, 80d, 17d), (BlobD, 80d, -60d, 21d) };
        foreach (var (blob, dx, dy, seconds) in blobs)
        {
            if (!on) { blob.BeginAnimation(TranslateTransform.XProperty, null); blob.BeginAnimation(TranslateTransform.YProperty, null); continue; }
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var x = new DoubleAnimation(blob.X, dx, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            var y = new DoubleAnimation(blob.Y, dy, TimeSpan.FromSeconds(seconds * 1.3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            Timeline.SetDesiredFrameRate(x, 15); Timeline.SetDesiredFrameRate(y, 15);
            blob.BeginAnimation(TranslateTransform.XProperty, x); blob.BeginAnimation(TranslateTransform.YProperty, y);
        }
        if (!on)
        {
            Pulse1Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); Pulse1Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Pulse1.BeginAnimation(OpacityProperty, null); Pulse1.Opacity = 0;
            return;
        }
        var duration = TimeSpan.FromSeconds(3.2);
        var grow = new DoubleAnimation(1, 1.6, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimationUsingKeyFrames { Duration = duration, RepeatBehavior = RepeatBehavior.Forever };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration)));
        grow.Duration = duration;
        Timeline.SetDesiredFrameRate(grow, 30); Timeline.SetDesiredFrameRate(fade, 30);
        Pulse1Scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow); Pulse1Scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Pulse1.BeginAnimation(OpacityProperty, fade);
    }
    protected override void OnActivated(EventArgs e) { base.OnActivated(e); SetAmbient(IsVisible && WindowState != WindowState.Minimized); }
    protected override void OnDeactivated(EventArgs e) { base.OnDeactivated(e); SetAmbient(false); }

    // ---------- "Đảo" thông báo ----------
    private void ShowIsland(IslandKind kind, string text, string? sub, double width, double holdSeconds = 0)
    {
        if (lifetime.IsCancellationRequested) return;
        islandTimer.Stop();
        IslandText.Text = text;
        IslandSub.Text = sub ?? "";
        IslandSub.Visibility = sub is null ? Visibility.Collapsed : Visibility.Visible;
        IslandSpinner.Visibility = kind == IslandKind.Busy ? Visibility.Visible : Visibility.Collapsed;
        IslandCheck.Visibility = kind == IslandKind.Done ? Visibility.Visible : Visibility.Collapsed;
        IslandWarn.Visibility = kind == IslandKind.Warn ? Visibility.Visible : Visibility.Collapsed;
        IslandSpin.BeginAnimation(RotateTransform.AngleProperty, kind == IslandKind.Busy && motion
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever } : null);
        Island.Visibility = Visibility.Visible;
        var height = sub is null ? 44.0 : 62.0;
        if (motion)
        {
            Island.BeginAnimation(WidthProperty, Spring(width, 620));
            Island.BeginAnimation(HeightProperty, Spring(height, 620));
            Island.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        }
        else
        {
            Island.BeginAnimation(WidthProperty, null); Island.BeginAnimation(HeightProperty, null); Island.BeginAnimation(OpacityProperty, null);
            Island.Width = width; Island.Height = height; Island.Opacity = 1;
        }
        if (holdSeconds > 0) { islandTimer.Interval = TimeSpan.FromSeconds(holdSeconds); islandTimer.Start(); }
    }
    private void HideIsland()
    {
        islandTimer.Stop();
        IslandSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(motion ? 240 : 1));
        fade.Completed += (_, _) => { if (Island.Opacity < 0.01) Island.Visibility = Visibility.Collapsed; };
        Island.BeginAnimation(OpacityProperty, fade);
        if (motion) Island.BeginAnimation(WidthProperty, new DoubleAnimation(160, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }
    private static DoubleAnimation Spring(double to, int ms) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6 } };

    // ---------- Nút cửa sổ tự vẽ ----------
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
}






