using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DriveLink.Services;
namespace DriveLink;

public sealed class UpdateWindow : Window
{
    private readonly AppUpdateFlow flow;
    private readonly Func<bool> prepare;
    private readonly Action restore;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private readonly DispatcherTimer idle = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, FontSize = 15 };
    private readonly TextBlock version = new(), notes = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border card = new() { Padding = new(16), CornerRadius = new(10), Margin = new(0, 18, 0, 16) };
    private readonly ProgressBar progress = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new(0, 0, 0, 16) };
    private readonly Button check = new() { Content = "Kiểm tra cập nhật" }, install = new(), cancel = new() { Content = "Hủy tải / Để sau" };
    private bool waiting, closed;
    public UpdateWindow(AppUpdateFlow flow, Func<bool> prepare, Action restore)
    {
        this.flow = flow; this.prepare = prepare; this.restore = restore;
        Title = "Cập nhật DriveLink — Quang Huy"; Icon = AppBrand.Icon; Width = 650; Height = Math.Min(660, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26) };
        panel.Children.Add(new TextBlock { Text = "Cập nhật DriveLink", FontSize = 26, FontWeight = FontWeights.SemiBold });
        version.Margin = new(0, 10, 0, 0); panel.Children.Add(version);
        card.Child = status; panel.Children.Add(card); panel.Children.Add(progress);
        panel.Children.Add(new ScrollViewer { Content = notes, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        check.Margin = new(0, 18, 0, 10); check.Click += async (_, _) => await RunAsync(flow.CheckAsync); panel.Children.Add(check);
        install.Style = (Style)FindResource("Primary"); install.Margin = new(0, 0, 0, 10); install.Click += async (_, _) => await InstallAsync(); panel.Children.Add(install);
        cancel.Click += (_, _) => { waiting = false; idle.Stop(); operation?.Cancel(); Render(); }; panel.Children.Add(cancel);
        var release = new Button { Content = "Mở trang phát hành", Margin = new(0, 10, 0, 0) };
        release.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(VelopackUpdateBackend.ReleasesUrl) { UseShellExecute = true }); } catch { status.Text = "Chưa mở được trình duyệt. Xem trang Releases của DriveLink trên GitHub."; } };
        panel.Children.Add(release);
        panel.Children.Add(new TextBlock { Text = "Khi cài, app sẽ chờ truyền file hoàn tất rồi khởi động lại. Tài khoản, cấu hình OAuth và cài đặt đã lưu được giữ tại vị trí hiện có. Không tự cập nhật cơ sở dữ liệu Cloud.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 16, 0, 0) });
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        flow.Changed += OnChanged;
        idle.Tick += (_, _) => TryInstall();
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; waiting = false; idle.Stop(); lifetime.Cancel(); operation?.Cancel(); flow.Changed -= OnChanged; };
        Render();
        Loaded += async (_, _) => { if (flow.Available is null && !flow.Busy) await RunAsync(flow.CheckAsync); };
    }
    private void OnChanged() { if (!closed) Dispatcher.BeginInvoke(Render); }
    private void Render()
    {
        if (closed) return;
        version.Text = $"Đang dùng: {AppBrand.Version}" + (flow.Available is { } update ? $"   →   Bản mới: {update.Version}" : "");
        status.Text = (flow.HasError ? "⚠ " : "● ") + flow.Message;
        status.Foreground = flow.HasError ? Brushes.DarkRed : Brushes.DarkBlue;
        card.Background = new SolidColorBrush(flow.HasError ? Color.FromRgb(255, 228, 230) : Color.FromRgb(231, 240, 255));
        notes.Text = flow.Available?.Notes ?? "";
        progress.Value = flow.Progress; progress.Visibility = flow.Busy || flow.Ready ? Visibility.Visible : Visibility.Collapsed;
        progress.IsIndeterminate = flow.Busy && flow.Available is null;
        check.IsEnabled = !flow.Busy && !waiting;
        install.Content = !flow.CanInstall ? "Tải bộ cài để bật cập nhật" : flow.Ready ? "Cài bản mới và khởi động lại" : "Tải và cập nhật";
        install.IsEnabled = !flow.Busy && !waiting && (!flow.CanInstall || flow.Available is not null);
        cancel.IsEnabled = flow.Busy || waiting;
    }
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (operation is not null || flow.Busy) return;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try { await work(operation.Token); }
        catch (OperationCanceledException) { }
        finally { operation.Dispose(); operation = null; Render(); }
    }
    private async Task InstallAsync()
    {
        if (!flow.CanInstall) { try { Process.Start(new ProcessStartInfo(VelopackUpdateBackend.ReleasesUrl) { UseShellExecute = true }); } catch { } return; }
        if (flow.Available is null) return;
        if (MessageBox.Show(this, $"Cập nhật lên DriveLink {flow.Available.Version}?\nApp sẽ chờ truyền file hoàn tất, lưu dữ liệu và khởi động lại. Hãy lưu các thay đổi cài đặt còn đang chỉnh trước khi tiếp tục.", "Cập nhật DriveLink", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!flow.Ready) await RunAsync(flow.DownloadAsync);
        if (flow.Ready && !closed) { waiting = true; idle.Start(); TryInstall(); }
    }
    private void TryInstall()
    {
        if (!waiting || closed) return;
        if (flow.TryApply(prepare, restore) || flow.HasError) { waiting = false; idle.Stop(); }
        Render();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        waiting = false; idle.Stop();
        if (operation is not null) { operation.Cancel(); e.Cancel = true; }
    }
}
