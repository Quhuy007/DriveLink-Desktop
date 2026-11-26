using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DriveLink.Ui;
namespace DriveLink.Preview;

public sealed class GlTask(string name, bool upload, Brush brush, double progress) : INotifyPropertyChanged
{
    private double value = progress;
    public string Name { get; } = name;
    public Brush Brush { get; } = brush;
    public string Direction { get; } = upload ? "" : "";
    public double Progress { get => value; set { this.value = value; Changed(nameof(Progress)); Changed(nameof(Percent)); } }
    public string Percent => value.ToString("0", CultureInfo.InvariantCulture) + "%";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed record GlSpace(string Name, Brush Dot, string Members, string Badge)
{
    public bool HasBadge => Badge.Length > 0;
}
public sealed record GlRecent(string Name, string Glyph, Brush Brush, Brush Soft, string Meta);

/// <summary>Bản dựng thử "kính lỏng" + lưới Bento. Mọi số liệu là minh họa; thả tệp vào chỉ đọc tên tệp, không gửi gì lên mạng.</summary>
public partial class GlassShell : Window
{
    private enum IslandKind { Busy, Done, Info }
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private const int CrossFiles = 24;
    private static readonly TimeSpan CrossDuration = TimeSpan.FromSeconds(3.6);
    private const int SparkPoints = 36;

    private readonly bool motion = SystemParameters.ClientAreaAnimation;
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer crossTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer islandTimer = new();
    private readonly DispatcherTimer dragTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly Random random = new(11);
    private readonly ObservableCollection<GlTask> tasks = [];
    private readonly List<double> upHistory = [], downHistory = [];
    private double up = 12, down = 46;
    private bool personal = true, crossRunning, dropActive;
    private DateTime crossStarted;

    private static Brush Hex(string color) { var brush = (Brush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    private static readonly Brush Blue = Hex("#8FB0FF"), Green = Hex("#4ADE80"), Violet = Hex("#A78BFA"), Amber = Hex("#FBBF24"), Teal = Hex("#2DD4BF"), Rose = Hex("#FB7185");
    private static readonly Brush BlueSoft = Hex("#265B8CFF"), GreenSoft = Hex("#224ADE80"), VioletSoft = Hex("#26A78BFA"), AmberSoft = Hex("#26FBBF24"), RoseSoft = Hex("#26FB7185");

    public GlassShell()
    {
        InitializeComponent();
        GlassCard.Motion = motion;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 40);
        Height = Math.Min(Height, area.Height - 40);
        for (var i = 0; i < SparkPoints; i++) { upHistory.Add(10 + Math.Sin(i / 3.0) * 3); downHistory.Add(44 + Math.Cos(i / 4.0) * 8); }
        tasks.Add(new GlTask("Ban_dung_ung_dung_v1.0.zip", true, Blue, 64));
        tasks.Add(new GlTask("Ghi_man_hinh_demo.mp4", true, Violet, 28));
        tasks.Add(new GlTask("Anh_chup_man_hinh_loi.png", false, Green, 86));
        TaskList.ItemsSource = tasks;
        RecentList.ItemsSource = new List<GlRecent>
        {
            new("Ban_dung_ung_dung_v1.0.zip", "", Amber, AmberSoft, "88,2 MB · 08:40"),
            new("Thiet_ke_logo_v4.png", "", Teal, GreenSoft, "7,7 MB · Hôm qua"),
            new("Hop_dong_thue_nha.pdf", "", Rose, RoseSoft, "1,1 MB · 20/11"),
            new("Ghi_man_hinh_demo.mp4", "", Violet, VioletSoft, "312,6 MB · Hôm qua"),
        };
        liveTimer.Tick += (_, _) => LiveTick();
        crossTimer.Tick += (_, _) => CrossTick();
        islandTimer.Tick += (_, _) => { islandTimer.Stop(); HideIsland(); };
        dragTimer.Tick += (_, _) => { dragTimer.Stop(); SetDrop(false); };
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            var frame = SystemParameters.WindowResizeBorderThickness;
            Root.Margin = maximized ? new Thickness(frame.Left + 3, frame.Top + 3, frame.Right + 3, frame.Bottom + 3) : new Thickness(0);
            MaxGlyph.Text = maximized ? "" : "";
        };
        Loaded += async (_, _) =>
        {
            ApplyAccount(false);
            LiveTick();
            liveTimer.Start();
            StartAmbient();
            PlayEntrance(70, 18);
            SetRing(RingPersonal, RingPersonalText, 0.39, 520);
            SetRing(RingWork, RingWorkText, 0.32, 640);
            await Task.Delay(1600);
            if (IsLoaded) RunCross();
        };
        Closed += (_, _) => { liveTimer.Stop(); crossTimer.Stop(); islandTimer.Stop(); dragTimer.Stop(); };
    }

    // ---------- Dữ liệu theo tài khoản ----------
    private void ApplyAccount(bool animate)
    {
        var hour = DateTime.Now.Hour;
        Greeting.Text = hour < 11 ? "Chào buổi sáng" : hour < 18 ? "Chào buổi chiều" : "Chào buổi tối";
        GreetingSub.Text = personal ? "3 tệp mới đang chờ trong “File giữa hai máy” · 2 máy đang trực tuyến" : "Tài khoản công ty · 1 thư mục vừa được gửi tới";
        SentToday.Text = personal ? "Đã gửi hôm nay 2,4 GB" : "Đã gửi hôm nay 640 MB";
        ReceivedToday.Text = personal ? "Đã nhận 6 tệp" : "Đã nhận 24 tệp";
        HeroSpace.Text = personal ? "File giữa hai máy" : "Phòng Kinh doanh";
        HeroDot.Fill = personal ? (Brush)FindResource("GlAccent") : Green;
        HeroMembers.Text = personal ? "2 máy · 3 tệp mới" : "6 thành viên";
        SpaceList.ItemsSource = personal
            ? new List<GlSpace> { new("File giữa hai máy", Blue, "2 máy · cập nhật 2 phút trước", "3 mới"), new("Dự án studio", Violet, "4 thành viên", ""), new("Gia đình", Amber, "5 thành viên · đã ghim", "") }
            : new List<GlSpace> { new("Phòng Kinh doanh", Green, "6 thành viên", "1 mới"), new("Hồ sơ dự án Ánh Dương", Teal, "3 thành viên", ""), new("Mẫu biểu nội bộ", Blue, "Toàn công ty", "") };
        CrossSourceLabel.Text = personal ? "NGUỒN · CÁ NHÂN" : "NGUỒN · CÔNG TY";
        CrossTargetLabel.Text = personal ? "ĐÍCH · CÔNG TY" : "ĐÍCH · CÁ NHÂN";
        if (!crossRunning) { CrossTargetInfo.Text = $"0/{CrossFiles} tệp"; CrossStatus.Text = "Sẵn sàng"; CrossStatus.Foreground = (Brush)FindResource("GlMuted"); }
        SegmentShift.BeginAnimation(TranslateTransform.XProperty, Spring(personal ? 0 : 104, 560));
        if (animate) PlayEntrance(35, 10);
    }

    private void PlayEntrance(int staggerMs, double distance)
    {
        var index = 0;
        foreach (var card in Bento.Children.OfType<GlassCard>())
            card.PlayEntrance(TimeSpan.FromMilliseconds(80 + index++ * staggerMs), distance);
    }

    // ---------- Chuyển động nền ----------
    private void StartAmbient()
    {
        if (!motion) return;
        Drift(BlobA, 90, 60, 19);
        Drift(BlobB, -100, -50, 23);
        Drift(BlobC, -70, 80, 17);
        Drift(BlobD, 80, -60, 21);
        PulseRing(Pulse1, Pulse1Scale, 0);
        PulseRing(Pulse2, Pulse2Scale, 1.3);
        LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    private static void Drift(TranslateTransform target, double dx, double dy, double seconds)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        target.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        target.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dy, TimeSpan.FromSeconds(seconds * 1.3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
    }

    private static void PulseRing(Ellipse ring, ScaleTransform scale, double delay)
    {
        var duration = TimeSpan.FromSeconds(2.6);
        var begin = TimeSpan.FromSeconds(delay);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ring.Opacity = 0;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.75, duration) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.75, duration) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, duration) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever });
    }

    private void SetRing(Ellipse ring, TextBlock label, double fraction, int delayMs)
    {
        // Độ dài nét tính theo bội số độ dày nét: chu vi vòng (đường kính 84 trừ nét) chia độ dày.
        var length = Math.PI * (84 - ring.StrokeThickness) / ring.StrokeThickness;
        ring.StrokeDashArray = [length, length];
        label.Text = (fraction * 100).ToString("0", Vi) + "%";
        var target = length * (1 - fraction);
        if (!motion) { ring.StrokeDashOffset = target; return; }
        ring.StrokeDashOffset = length;
        ring.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(length, target, TimeSpan.FromMilliseconds(1200))
        { BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---------- Số liệu trực tiếp ----------
    private void LiveTick()
    {
        up = Math.Clamp(up + (random.NextDouble() - 0.5) * 3, 5, 18);
        down = Math.Clamp(down + (random.NextDouble() - 0.5) * 9, 24, 64);
        upHistory.Add(up); downHistory.Add(down);
        if (upHistory.Count > SparkPoints) { upHistory.RemoveAt(0); downHistory.RemoveAt(0); }
        UpSpeed.Text = string.Format(Vi, "{0:0.0} MB/s", up);
        DownSpeed.Text = string.Format(Vi, "{0:0.0} MB/s", down);
        foreach (var task in tasks)
            task.Progress = task.Progress >= 100 ? 0 : Math.Min(100, task.Progress + 0.8 + random.NextDouble() * 2.6);
        TaskCount.Text = tasks.Count.ToString(Vi) + " tác vụ";
        DrawSpark();
    }

    private void DrawSpark()
    {
        double width = Spark.ActualWidth, height = Spark.ActualHeight;
        if (width <= 0 || height <= 0) return;
        PointCollection Line(List<double> values, double max)
        {
            var points = new PointCollection(values.Count);
            for (var i = 0; i < values.Count; i++) points.Add(new Point(i * width / (SparkPoints - 1), height - 4 - values[i] / max * (height - 12)));
            return points;
        }
        var upLine = Line(upHistory, 20);
        SparkUp.Points = upLine;
        SparkDown.Points = Line(downHistory, 70);
        var fill = new PointCollection(upLine) { new Point(width, height), new Point(0, height) };
        SparkFill.Points = fill;
    }

    private void Spark_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSpark();

    // ---------- Gửi chéo ----------
    private void Cross_Click(object sender, RoutedEventArgs e) => RunCross();

    private void RunCross()
    {
        if (crossRunning) return;
        crossRunning = true;
        crossStarted = DateTime.UtcNow;
        CrossButton.IsEnabled = false;
        CrossButtonText.Text = "Đang sao chép";
        CrossCard.IsHighlighted = true;
        CrossStatus.Foreground = (Brush)FindResource("GlMuted");
        StartFlow(true);
        ShowIsland(IslandKind.Busy, "Đang gửi chéo · 0%", null, 250);
        crossTimer.Start();
    }

    private void CrossTick()
    {
        var elapsed = DateTime.UtcNow - crossStarted;
        var linear = Math.Min(1, elapsed / CrossDuration);
        var progress = 1 - Math.Pow(1 - linear, 1.6);
        var files = (int)Math.Round(progress * CrossFiles);
        CrossTargetInfo.Text = $"{files}/{CrossFiles} tệp";
        CrossStatus.Text = string.Format(Vi, "{0:0}% · {1}/{2} tệp", progress * 100, files, CrossFiles);
        IslandText.Text = string.Format(Vi, "Đang gửi chéo · {0:0}%", progress * 100);
        if (linear < 1) return;
        crossTimer.Stop();
        crossRunning = false;
        CrossButton.IsEnabled = true;
        CrossButtonText.Text = "Gửi lại";
        CrossCard.IsHighlighted = false;
        StartFlow(false);
        CrossStatus.Text = string.Format(Vi, "Hoàn tất · {0:0.0}s", CrossDuration.TotalSeconds);
        CrossStatus.Foreground = Green;
        var target = personal ? "Công ty" : "Cá nhân";
        ShowIsland(IslandKind.Done, "Gửi chéo hoàn tất · Đã thu hồi quyền tạm",
            string.Format(Vi, "{0}/{0} tệp sang {1} · {2:0.0}s · 0 MB qua máy này", CrossFiles, target, CrossDuration.TotalSeconds), 470, 4.5);
    }

    private void Flow_SizeChanged(object sender, SizeChangedEventArgs e) => StartFlow(crossRunning);

    /// <summary>Các hạt sáng chạy dọc đường nối nguồn → đích; chạy nhanh và rõ hơn khi đang sao chép.</summary>
    private void StartFlow(bool fast)
    {
        FlowCanvas.Children.Clear();
        double width = FlowCanvas.ActualWidth, height = FlowCanvas.ActualHeight;
        if (width <= 0) return;
        const int count = 6;
        var seconds = fast ? 0.9 : 2.8;
        for (var i = 0; i < count; i++)
        {
            var shift = new TranslateTransform(motion ? -20 : width * (i + 0.5) / count, 0);
            var dot = new Ellipse
            {
                Width = fast ? 8 : 6, Height = fast ? 8 : 6, Opacity = fast ? 1 : 0.45, RenderTransform = shift,
                Fill = i % 2 == 0 ? (Brush)FindResource("GlAccent") : Violet,
            };
            if (fast) dot.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x5B, 0x8C, 0xFF), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 };
            Canvas.SetTop(dot, (height - dot.Height) / 2);
            FlowCanvas.Children.Add(dot);
            if (!motion) continue;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-10, width + 10, TimeSpan.FromSeconds(seconds))
            {
                BeginTime = TimeSpan.FromSeconds(seconds * i / count), RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
    }

    // ---------- "Đảo" thông báo ----------
    private void ShowIsland(IslandKind kind, string text, string? sub, double width, double holdSeconds = 0)
    {
        islandTimer.Stop();
        IslandText.Text = text;
        IslandSub.Text = sub ?? "";
        IslandSub.Visibility = sub is null ? Visibility.Collapsed : Visibility.Visible;
        IslandSpinner.Visibility = kind == IslandKind.Busy ? Visibility.Visible : Visibility.Collapsed;
        IslandCheck.Visibility = kind == IslandKind.Done ? Visibility.Visible : Visibility.Collapsed;
        IslandInfo.Visibility = kind == IslandKind.Info ? Visibility.Visible : Visibility.Collapsed;
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
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(motion ? 240 : 1));
        fade.Completed += (_, _) => { if (Island.Opacity < 0.01) Island.Visibility = Visibility.Collapsed; };
        Island.BeginAnimation(OpacityProperty, fade);
        if (motion) Island.BeginAnimation(WidthProperty, new DoubleAnimation(160, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private static DoubleAnimation Spring(double to, int ms) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6 } };

    // ---------- Kéo thả tệp ----------
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!files) return;
        SetDrop(true);
        dragTimer.Stop();
        dragTimer.Start();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        dragTimer.Stop();
        SetDrop(false);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        // Chỉ lấy tên để hiển thị; không đọc nội dung tệp và không gửi đi đâu.
        foreach (var path in paths.Take(3).Reverse()) tasks.Insert(0, new GlTask(System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)), true, Blue, 0));
        while (tasks.Count > 4) tasks.RemoveAt(tasks.Count - 1);
        TaskCount.Text = tasks.Count.ToString(Vi) + " tác vụ";
        if (motion) Bounce(OrbScale, 1.22);
        ShowIsland(IslandKind.Info, $"Đã thêm {paths.Length} mục vào hàng đợi minh họa", "Bản thử: chưa gửi thật lên Google Drive", 400, 3.5);
    }

    private void SetDrop(bool active)
    {
        if (dropActive == active) return;
        dropActive = active;
        HeroCard.IsHighlighted = active;
        DropTitle.Text = active ? "Thả để gửi" : "Thả tệp hoặc thư mục vào đây";
        DropHint.Text = active ? $"Tệp sẽ vào “{HeroSpace.Text}”" : "DriveLink gửi lên Google Drive, máy kia nhận trong vài giây";
        if (!motion) { OrbScale.ScaleX = OrbScale.ScaleY = active ? 1.14 : 1; return; }
        OrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, Spring(active ? 1.14 : 1, 600));
        OrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, Spring(active ? 1.14 : 1, 600));
    }

    private static void Bounce(ScaleTransform scale, double peak)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 5 }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    // ---------- Điều hướng và nút ----------
    private void Account_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        personal = ReferenceEquals(sender, SegPersonal);
        ApplyAccount(true);
    }

    private void Dock_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not FrameworkElement { Tag: string tag }) return;
        var index = int.Parse(tag, CultureInfo.InvariantCulture);
        DockShift.BeginAnimation(TranslateTransform.YProperty, Spring(index * 52, 620));
        if (index != 0) ShowIsland(IslandKind.Info, "Bản thử mới dựng màn Tổng quan", null, 310, 2.2);
    }

    private void Placeholder_Click(object sender, RoutedEventArgs e)
    {
        var text = (sender as FrameworkElement)?.Tag as string ?? "Bản thử: chưa nối chức năng";
        ShowIsland(IslandKind.Info, text, null, Math.Clamp(90 + text.Length * 7.4, 240, 520), 2.4);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
