using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
namespace DriveLink;

public partial class LoginStatusWindow : Window
{
    private readonly Action cancel;
    private readonly CancellationTokenSource closed = new();
    private bool completed;
    private bool ownerClosing;
    public LoginStatusWindow(Action cancel)
    {
        InitializeComponent(); this.cancel = cancel;
        Loaded += (_, _) => { if (Owner is not null) Owner.Closing += Owner_Closing; };
        Closed += (_, _) => { closed.Cancel(); if (Owner is not null) Owner.Closing -= Owner_Closing; };
    }
    public async Task CompleteAsync(bool success, string detail)
    {
        if (completed || closed.IsCancellationRequested) return;
        completed = true;
        StateTitle.Text = success ? "✓ Đăng nhập thành công" : "! Đăng nhập không thành công";
        StateTitle.Foreground = new SolidColorBrush(success ? Color.FromRgb(22, 132, 100) : Color.FromRgb(211, 70, 85));
        StateDetail.Text = detail; StateDetail.ToolTip = detail;
        WaitingProgress.IsIndeterminate = false; WaitingProgress.Value = 100;
        WaitingProgress.Foreground = StateTitle.Foreground;
        CancelLogin.Visibility = Visibility.Collapsed;
        try
        {
            for (int remaining = 3; remaining > 0; remaining--)
            {
                Countdown.Text = $"Tự đóng sau {remaining} giây…";
                await Task.Delay(TimeSpan.FromSeconds(1), closed.Token);
            }
            Close();
        }
        catch (OperationCanceledException) { }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancel();
    private void Owner_Closing(object? sender, CancelEventArgs e) { if (!e.Cancel) { ownerClosing = true; closed.Cancel(); } }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (completed || ownerClosing || Application.Current.Dispatcher.HasShutdownStarted) return;
        e.Cancel = true; CancelLogin.IsEnabled = false; cancel();
    }
}
