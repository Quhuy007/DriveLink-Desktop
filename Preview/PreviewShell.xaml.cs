using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace DriveLink.Preview;

public sealed record PreviewEntry(string Glyph, Brush GlyphBrush, string Name, string Owner, string Modified, string Size,
    bool Starred = false, bool Shared = false, bool IsNew = false);

/// <summary>Cửa sổ dựng thử giao diện tối. Toàn bộ dữ liệu là minh họa; không đọc tài khoản và không gọi mạng.</summary>
public partial class PreviewShell : Window
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private const int CrossFiles = 24;
    private static readonly TimeSpan CrossDuration = TimeSpan.FromSeconds(3.6);
    private const string CrossFolder = "Video quay dựng 2026";

    private readonly DispatcherTimer transferTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly Random random = new(7);
    private DateTime transferStarted;
    private bool transferDone, workReceived, personal = true;
    private string view = "drive";
    private List<PreviewEntry> current = [];
    private double taskProgress = 0.42;

    public PreviewShell()
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 40);
        Height = Math.Min(Height, area.Height - 40);
        VersionChip.Text = "v" + AppBrand.Version + " · thử";
        transferTimer.Tick += (_, _) => TransferTick();
        statusTimer.Tick += (_, _) => StatusTick();
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); HideToast(); };
        StateChanged += (_, _) =>
        {
            // Cửa sổ tự vẽ khung bị tràn mép khi phóng to; chừa lề bằng đúng độ dày khung thay đổi kích thước.
            var maximized = WindowState == WindowState.Maximized;
            var frame = SystemParameters.WindowResizeBorderThickness;
            Root.Margin = maximized ? new Thickness(frame.Left + 3, frame.Top + 3, frame.Right + 3, frame.Bottom + 3) : new Thickness(0);
            MaxGlyph.Text = maximized ? "" : "";
        };
        Loaded += async (_, _) =>
        {
            ApplyAccount();
            StatusTick();
            statusTimer.Start();
            await Task.Delay(900);
            if (IsLoaded) PlayCrossTransfer();
        };
        Closed += (_, _) => { transferTimer.Stop(); statusTimer.Stop(); hideTimer.Stop(); };
    }

    private Brush Res(string key) => (Brush)FindResource(key);
    private static Brush Hex(string color) { var brush = (Brush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    private static readonly Brush FolderBrush = Hex("#5B8DEF"), ImageBrush = Hex("#3FBF9F"), VideoBrush = Hex("#E46A6A"), DocBrush = Hex("#8FA3C7"),
        ZipBrush = Hex("#E0A43A"), PdfBrush = Hex("#EF6A5A"), SheetBrush = Hex("#3DBA6F"), AudioBrush = Hex("#B07CFF"), SpaceBrush = Hex("#4C7DFF");
    private const string Folder = "", Doc = "", Image = "", Video = "", Zip = "", Audio = "", Link = "";

    private static string Them(string email) => DisplayPrivacy.MaskEmail(email);

    private List<PreviewEntry> PersonalDrive() =>
    [
        new(Folder, SpaceBrush, "[DriveLink] File giữa hai máy", "Tôi", "Hôm nay 09:12", "57 mục · 4,8 GB", Shared: true),
        new(Folder, FolderBrush, "Hợp đồng & hóa đơn", "Tôi", "Hôm qua", "46 mục · 1,2 GB", Starred: true),
        new(Folder, FolderBrush, "Ảnh gia đình", "Tôi", "22/11/2026", "1.204 mục · 38,6 GB", Shared: true),
        new(Folder, FolderBrush, CrossFolder, "Tôi", "21/11/2026", "24 mục · 153,7 GB", Starred: true),
        new(Folder, FolderBrush, "Sao lưu máy tính", "Tôi", "18/11/2026", "212 mục · 96,4 GB"),
        new(Folder, FolderBrush, "Tài liệu học tập", "Tôi", "12/11/2026", "88 mục · 2,3 GB"),
        new(Zip, ZipBrush, "Ban_dung_ung_dung_v1.0.zip", "Tôi", "Hôm nay 08:40", "88,2 MB"),
        new(Doc, SheetBrush, "BaoCao_Quy3_2026.xlsx", "Tôi", "Hôm qua", "2,4 MB", Shared: true),
        new(Image, ImageBrush, "Thiet_ke_logo_v4.png", "Tôi", "23/11/2026", "7,7 MB", Starred: true),
        new(Doc, PdfBrush, "Hop_dong_thue_nha.pdf", Them("lan.nguyen@example.com"), "20/11/2026", "1,1 MB", Shared: true),
        new(Doc, DocBrush, "Ghi chú cuộc họp", "Tôi", "19/11/2026", "Google Tài liệu"),
        new(Image, ImageBrush, "IMG_2031.jpg", "Tôi", "17/11/2026", "5,4 MB"),
        new(Audio, AudioBrush, "Nhac_nen_video.mp3", "Tôi", "15/11/2026", "9,8 MB"),
        new(Doc, DocBrush, "Ke_hoach_nam_2027.docx", "Tôi", "10/11/2026", "640 KB"),
        new(Doc, SheetBrush, "Danh_sach_khach_moi.xlsx", "Tôi", "02/11/2026", "88 KB", Starred: true),
    ];

    private List<PreviewEntry> WorkDrive() =>
    [
        new(Folder, FolderBrush, CrossFolder, "Tôi", "Vừa xong", "24 mục · 153,7 GB", IsNew: workReceived),
        new(Folder, FolderBrush, "Phòng Kinh doanh", "Tôi", "Hôm nay 10:05", "318 mục · 12,1 GB", Shared: true),
        new(Folder, FolderBrush, "Hồ sơ dự án Ánh Dương", "Tôi", "Hôm qua", "96 mục · 6,4 GB", Starred: true, Shared: true),
        new(Folder, FolderBrush, "Mẫu biểu nội bộ", "Tôi", "14/11/2026", "41 mục · 220 MB"),
        new(Doc, PdfBrush, "Quy_trinh_onboarding.pdf", "Tôi", "Hôm nay 09:30", "3,2 MB", Shared: true),
        new(Doc, SheetBrush, "Bao_gia_Q4.xlsx", Them("minh.tran@congty.example"), "Hôm qua", "410 KB", Shared: true),
        new(Video, VideoBrush, "Hop_toan_cong_ty_11-2026.mp4", "Tôi", "22/11/2026", "1,8 GB"),
        new(Doc, DocBrush, "Biên bản họp tuần", "Tôi", "21/11/2026", "Google Tài liệu", Starred: true),
    ];

    private List<PreviewEntry> Data(string key)
    {
        var drive = personal ? PersonalDrive() : WorkDrive();
        if (!personal && !workReceived) drive.RemoveAt(0);
        return key switch
        {
            "drive" => drive,
            "starred" => drive.Where(x => x.Starred).ToList(),
            "links" => drive.Where(x => x.Shared).ToList(),
            "recent" => drive.Where(x => x.Glyph != Folder).ToList(),
            "trash" => [],
            "shared-drives" =>
            [
                new(Folder, SheetBrush, "Marketing chung", "Drive dùng chung", "Hôm qua", "1.120 mục · 64,2 GB"),
                new(Folder, SheetBrush, "Kỹ thuật & sản phẩm", "Drive dùng chung", "20/11/2026", "860 mục · 31,5 GB"),
            ],
            "shared-with-me" =>
            [
                new(Folder, FolderBrush, "Ảnh cưới Hà & Nam", Them("ha.pham@example.com"), "Hôm qua", "380 mục · 22,9 GB"),
                new(Doc, PdfBrush, "Đề cương khóa học.pdf", Them("thay.binh@example.com"), "19/11/2026", "2,1 MB"),
                new(Link, DocBrush, "Lịch trực tháng 12", Them("lan.nguyen@example.com"), "18/11/2026", "Google Trang tính"),
            ],
            "space-machines" =>
            [
                new(Zip, ZipBrush, "Ban_dung_ung_dung_v1.0.zip", "Tôi", "Hôm nay 08:40", "88,2 MB", IsNew: true),
                new(Image, ImageBrush, "Anh_chup_man_hinh_loi.png", Them("maynhan@example.com"), "Hôm nay 08:12", "1,3 MB", IsNew: true),
                new(Doc, PdfBrush, "Huong_dan_cai_dat.pdf", Them("maynhan@example.com"), "Hôm nay 07:55", "740 KB", IsNew: true),
                new(Video, VideoBrush, "Ghi_man_hinh_demo.mp4", "Tôi", "Hôm qua", "312,6 MB"),
                new(Zip, ZipBrush, "Du_lieu_khach_hang.zip", Them("maynhan@example.com"), "22/11/2026", "1,4 GB"),
            ],
            "space-studio" =>
            [
                new(Video, VideoBrush, "Phim_ngan_ban_dung_3.mp4", Them("studio@example.com"), "Hôm qua", "4,2 GB"),
                new(Audio, AudioBrush, "Am_thanh_tong_hop.wav", "Tôi", "20/11/2026", "612 MB"),
            ],
            "space-family" =>
            [
                new(Image, ImageBrush, "Tet_2026_ca_nha.jpg", Them("me@example.com"), "16/11/2026", "6,1 MB", Starred: true),
            ],
            _ => drive,
        };
    }

    private static (string Title, string Glyph) Describe(string key) => key switch
    {
        "drive" => ("Drive của tôi", Folder),
        "shared-drives" => ("Drive dùng chung", ""),
        "recent" => ("Gần đây", ""),
        "starred" => ("Có gắn dấu sao", ""),
        "shared-with-me" => ("Được chia sẻ với tôi", ""),
        "links" => ("Liên kết công khai", Link),
        "trash" => ("Thùng rác", ""),
        "space-machines" => ("File giữa hai máy", ""),
        "space-studio" => ("Dự án studio", ""),
        "space-family" => ("Gia đình", ""),
        _ => ("Drive của tôi", Folder),
    };

    private void ApplyAccount()
    {
        AccountName.Text = personal ? "Cá nhân" : "Công ty";
        AccountEmail.Text = Them(personal ? "quanghuy.canhan@example.com" : "quanghuy@congty.example");
        AvatarText.Text = personal ? "CN" : "CT";
        AvatarBg.Background = Res(personal ? "PvAccent" : "PvSuccess");
        CrumbDot.Fill = AvatarBg.Background;
        CrumbAccount.Text = AccountName.Text;
        QuotaBar.Foreground = AvatarBg.Background;
        QuotaBar.Value = personal ? 39 : 32;
        QuotaText.Text = personal ? "778,2 GB / 2 TB" : "1,62 TB / 5 TB";
        QuotaPercent.Text = QuotaBar.Value.ToString("0", Vi) + "%";
        ShowView();
    }

    private void ShowView()
    {
        var (title, glyph) = Describe(view);
        CrumbTitle.Text = title;
        CrumbIcon.Text = glyph;
        current = Data(view);
        var query = SearchBox.Text.Trim();
        var shown = query.Length == 0 ? current : current.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || x.Owner.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        FileList.ItemsSource = shown;
        CountText.Text = shown.Count.ToString(Vi) + " mục";
        var empty = shown.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        if (query.Length > 0) { EmptyIcon.Text = ""; EmptyTitle.Text = "Không có kết quả"; EmptyHint.Text = "Thử từ khóa khác hoặc tìm trong toàn bộ Drive."; }
        else if (view == "trash") { EmptyIcon.Text = ""; EmptyTitle.Text = "Thùng rác trống"; EmptyHint.Text = "Mục đã xóa được Google Drive giữ 30 ngày trước khi xóa hẳn."; }
        else { EmptyIcon.Text = ""; EmptyTitle.Text = "Chưa có mục nào"; EmptyHint.Text = "Kéo thả tệp vào đây để tải lên."; }
    }

    private void AccountTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        personal = ReferenceEquals(sender, TabPersonal);
        if (!personal) WorkBadge.Visibility = Visibility.Collapsed;
        ApplyAccount();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not FrameworkElement { Tag: string key }) return;
        view = key;
        ShowView();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) ShowView();
    }

    private void Cross_Click(object sender, RoutedEventArgs e) => PlayCrossTransfer();

    private void PlayCrossTransfer()
    {
        hideTimer.Stop();
        transferTimer.Stop();
        transferDone = false;
        Toast.BorderBrush = Res("PvAccent");
        ToastSpinner.Visibility = Visibility.Visible;
        ToastDoneIcon.Visibility = Visibility.Collapsed;
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        ToastTitle.Text = $"Đang gửi “{CrossFolder}” sang Công ty";
        ToastSource.Text = "Cá nhân · 153,7 GB";
        ToastProgress.Value = 0;
        ToastStatus.Foreground = Res("PvAccent");
        UpdateTransfer(0, TimeSpan.Zero);
        ShowToast();
        transferStarted = DateTime.UtcNow;
        transferTimer.Start();
    }

    private void TransferTick()
    {
        var elapsed = DateTime.UtcNow - transferStarted;
        var linear = Math.Min(1, elapsed / CrossDuration);
        UpdateTransfer(1 - Math.Pow(1 - linear, 1.6), elapsed);
        if (linear >= 1) { transferTimer.Stop(); CompleteTransfer(); }
    }

    private void UpdateTransfer(double progress, TimeSpan elapsed)
    {
        ToastProgress.Value = progress * 100;
        ToastTarget.Text = $"Công ty · {(int)Math.Round(progress * CrossFiles)}/{CrossFiles} tệp";
        ToastStatus.Text = string.Format(Vi, "{0:0}% · {1:0.0}s", progress * 100, elapsed.TotalSeconds);
    }

    private void CompleteTransfer()
    {
        transferDone = true;
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        ToastSpinner.Visibility = Visibility.Collapsed;
        ToastDoneIcon.Visibility = Visibility.Visible;
        Toast.BorderBrush = Hex("#AA34C77B");
        ToastTitle.Text = "Gửi chéo hoàn tất · Đã thu hồi quyền tạm";
        ToastStatus.Foreground = Res("PvSuccess");
        ToastStatus.Text = string.Format(Vi, "100% · Hoàn tất trong {0:0.0}s", CrossDuration.TotalSeconds);
        workReceived = true;
        if (personal) WorkBadge.Visibility = Visibility.Visible; else ShowView();
        if (!Toast.IsMouseOver) hideTimer.Start();
    }

    private void ShowToast()
    {
        Toast.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private void HideToast()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => { if (Toast.Opacity == 0) Toast.Visibility = Visibility.Collapsed; };
        Toast.BeginAnimation(OpacityProperty, fade);
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, TimeSpan.FromMilliseconds(200)));
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e) { hideTimer.Stop(); HideToast(); }
    private void Toast_MouseEnter(object sender, MouseEventArgs e) => hideTimer.Stop();
    private void Toast_MouseLeave(object sender, MouseEventArgs e) { if (transferDone && Toast.Visibility == Visibility.Visible) hideTimer.Start(); }

    private void StatusTick()
    {
        taskProgress += 0.012 + random.NextDouble() * 0.02;
        if (taskProgress > 0.99) taskProgress = 0.08;
        var up = 9 + random.NextDouble() * 6;
        var down = 30 + random.NextDouble() * 25;
        UpSpeed.Text = string.Format(Vi, "{0:0.0} MB/s", up);
        DownSpeed.Text = string.Format(Vi, "{0:0.0} MB/s", down);
        TaskText.Text = string.Format(Vi, "3 tác vụ · {0:0}%", taskProgress * 100);
        TaskBar.Value = taskProgress * 100;
        var remaining = TimeSpan.FromSeconds((1 - taskProgress) * 70);
        EtaText.Text = "Còn " + remaining.ToString(@"mm\:ss", Vi);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
