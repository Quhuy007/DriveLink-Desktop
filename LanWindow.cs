using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DriveLink.Services;
using DriveLink.Services.Lan;
using DriveLink.Ui;
using Microsoft.Win32;
namespace DriveLink;

/// <summary>Thiết bị trong mạng LAN: bật/tắt, ghép nối bằng mã 6 số, gửi tệp trực tiếp (không qua Google), bỏ ghép nối.</summary>
public sealed class LanWindow : Window
{
    private readonly LanService lan;
    private readonly LanTrustStore trust;
    private readonly ListBox list = new() { MinHeight = 200, Margin = new(0, 10, 0, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new(0, 6, 0, 0) };
    private readonly ProgressBar progress = new() { Height = 6, Maximum = 100, Margin = new(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Action<IReadOnlyList<(string Name, long Bytes, TimeSpan Elapsed, bool Ok, string Note)>> recordActivity;
    private bool working;

    public LanWindow(LanService lan, LanTrustStore trust, bool enabled, Func<bool, string?> setEnabled, Action<IReadOnlyList<(string Name, long Bytes, TimeSpan Elapsed, bool Ok, string Note)>> recordActivity)
    {
        this.lan = lan; this.trust = trust; this.recordActivity = recordActivity;
        Theme.Apply(this);
        Title = "Thiết bị trong mạng LAN — DriveLink"; Width = 620; Height = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var toggle = new CheckBox { Content = "Bật truyền thẳng trong mạng LAN (cho máy khác tìm thấy và gửi trực tiếp)", IsChecked = enabled, Margin = new(0, 10, 0, 6) };
        toggle.Click += (_, _) =>
        {
            var error = setEnabled(toggle.IsChecked == true);
            if (error is not null) { toggle.IsChecked = false; status.Text = error; }
            Render();
        };
        var pair = new Button { Content = "Ghép nối", Padding = new(14, 5, 14, 5), Margin = new(0, 0, 8, 0) };
        var send = new Button { Content = "Gửi tệp…", Padding = new(14, 5, 14, 5), Margin = new(0, 0, 8, 0) };
        var unpair = new Button { Content = "Bỏ ghép nối", Padding = new(14, 5, 14, 5) };
        pair.Click += async (_, _) => await PairAsync();
        send.Click += async (_, _) => await SendAsync();
        unpair.Click += (_, _) => Unpair();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(pair); buttons.Children.Add(send); buttons.Children.Add(unpair);

        var panel = new DockPanel { Margin = new(22) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Truyền thẳng trong mạng LAN", FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = $"Máy này: {lan.Identity.Name} · mã thiết bị {lan.Identity.Id[..8]}", Foreground = Theme.Muted, Margin = new(0, 4, 0, 0) });
        top.Children.Add(toggle);
        top.Children.Add(new TextBlock
        {
            Text = "Hai máy cùng mạng gửi tệp cho nhau trực tiếp, nhanh hơn và không tốn dung lượng Google Drive. Lần đầu cần ghép nối: bấm Ghép nối trên một máy, rồi so mã 6 số hiện trên CẢ HAI máy và xác nhận. " +
                   "Chỉ máy đã ghép nối mới gửi được; dữ liệu được mã hóa trên đường truyền và kiểm SHA-256 khi nhận. Tệp nhận được nằm trong Hộp thư đến\\LAN. Windows có thể hỏi cho phép DriveLink qua tường lửa: chọn Mạng riêng (Private).",
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, FontSize = 12,
        });
        DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var bottom = new StackPanel();
        bottom.Children.Add(buttons); bottom.Children.Add(progress); bottom.Children.Add(status);
        DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        panel.Children.Add(list);
        Content = panel;

        lan.DevicesChanged += OnDevices;
        refresh.Tick += (_, _) => Render();
        Loaded += (_, _) => { Render(); refresh.Start(); };
        Closed += (_, _) => { lan.DevicesChanged -= OnDevices; refresh.Stop(); };
    }

    private void OnDevices() => Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        var selected = (list.SelectedItem as LanDevice)?.Id;
        var devices = lan.Devices.ToList();
        // Thiết bị đã ghép nối nhưng chưa thấy trong mạng vẫn hiện để bỏ ghép nối.
        foreach (var known in trust.All.Where(t => devices.All(d => d.Id != t.Id)))
            devices.Add(new LanDevice { Id = known.Id, Name = known.Name + " (không thấy trong mạng)", Trusted = true });
        list.ItemsSource = devices;
        list.SelectedItem = devices.FirstOrDefault(d => d.Id == selected) ?? devices.FirstOrDefault();
        if (!working) status.Text = !lan.IsRunning ? "Đang tắt. Bật ở trên để tìm máy khác trong mạng." : devices.Count == 0 ? "Đang tìm máy DriveLink khác trong mạng… (máy kia cũng cần bật LAN)" : $"Thấy {devices.Count(d => d.LastSeen > DateTimeOffset.MinValue)} máy trong mạng.";
    }

    private LanDevice? Online => list.SelectedItem is LanDevice { LastSeen: var seen } d && seen > DateTimeOffset.MinValue ? d : null;

    private async Task PairAsync()
    {
        if (working || Online is not { } device) { status.Text = "Chọn một máy đang có trong mạng."; return; }
        working = true; status.Text = "Đang ghép nối với " + device.Name + "… Hãy xem mã trên cả hai máy.";
        try
        {
            var ok = await lan.PairAsync(device.EndPoint, (name, code) => Dispatcher.InvokeAsync(() => MessageBox.Show(this,
                $"Ghép nối với “{name}”.\n\nMã trên máy này: {code}\n\nMã trên máy kia có giống hệt không? Chỉ bấm Có khi hai mã trùng nhau.", "So mã ghép nối", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes).Task, CancellationToken.None);
            status.Text = ok ? "Đã ghép nối với " + device.Name + "." : "Chưa ghép nối (một bên đã từ chối hoặc mã không khớp).";
        }
        catch (Exception ex) { status.Text = "Không ghép nối được: " + ex.Message; }
        finally { working = false; Render(); }
    }

    private async Task SendAsync()
    {
        if (working || Online is not { Trusted: true } device) { status.Text = "Chọn một máy đã ghép nối và đang có trong mạng."; return; }
        var picker = new OpenFileDialog { Multiselect = true, Title = "Chọn tệp gửi thẳng tới " + device.Name };
        if (picker.ShowDialog(this) != true) return;
        working = true; progress.Visibility = Visibility.Visible;
        // Cả lô đi qua một kết nối; nhật ký Hoạt động được ghi một lần khi xong (không ghi đĩa mỗi tệp).
        var files = picker.FileNames; var finished = false; var delivered = 0;
        var log = new System.Collections.Concurrent.ConcurrentQueue<(string Name, long Bytes, TimeSpan Elapsed, bool Ok, string Note)>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = new Progress<LanSendProgress>(p =>
        {
            if (finished) return;
            progress.Value = p.Overall.Percent;
            status.Text = $"Đang gửi {p.Name}" + (p.Count > 1 ? $" ({p.Index + 1}/{p.Count})" : "") + $" · {p.Overall.AmountLabel} · {p.Overall.SpeedLabel}";
        });
        try
        {
            await lan.SendFilesAsync(device.EndPoint, files, report, (path, size, elapsed) =>
            {
                log.Enqueue((Path.GetFileName(path), size, elapsed, true, "LAN → " + device.Name));
                Interlocked.Increment(ref delivered);
            }, CancellationToken.None);
            finished = true;
            var bytes = log.Sum(e => e.Bytes);
            status.Text = $"Đã gửi thẳng {files.Length} tệp ({DriveItem.FormatSize(bytes)}) tới {device.Name} trong {watch.Elapsed.TotalSeconds:0.#} giây"
                + (watch.Elapsed.TotalSeconds > 0.5 ? $" · trung bình {DriveItem.FormatSize((long)(bytes / watch.Elapsed.TotalSeconds))}/s." : ".");
        }
        catch (Exception ex)
        {
            finished = true;
            var name = Path.GetFileName(files[Math.Min(Volatile.Read(ref delivered), files.Length - 1)]);
            log.Enqueue((name, 0, watch.Elapsed, false, ex.Message));
            status.Text = (delivered > 0 ? $"Đã gửi {delivered}/{files.Length} tệp. " : "") + $"Chưa gửi được {name}: {ex.Message}";
        }
        finally { recordActivity(log.ToList()); working = false; progress.Visibility = Visibility.Collapsed; }
    }

    private void Unpair()
    {
        if (list.SelectedItem is not LanDevice { Trusted: true } device) { status.Text = "Chọn một máy đã ghép nối."; return; }
        if (MessageBox.Show(this, $"Bỏ ghép nối với “{device.Name}”? Hai máy sẽ không gửi thẳng cho nhau được nữa cho tới khi ghép nối lại.", "Bỏ ghép nối", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        trust.Remove(device.Id);
        SecurityLog.Write("lan-unpaired", "Bỏ ghép nối LAN với thiết bị " + device.Id);
        Render();
    }
}
