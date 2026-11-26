using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DriveLink.Services;
using DriveLink.Ui;
namespace DriveLink;

public sealed class AdminSignInWindow : Window
{
    private readonly PasswordBox password = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly Button submit = new() { Content = "Mở quản trị", IsDefault = true };
    private readonly CancellationTokenSource lifetime = new();
    private readonly AdminLockout lockout = new();
    private SupabaseConfigService? candidate;
    private readonly Func<SupabaseConfigService> createService;
    private bool busy;
    public SupabaseConfigService? Session { get; private set; }
    public string Failure { get; private set; } = "";
    public AdminSignInWindow(Func<SupabaseConfigService> createService)
    {
        this.createService = createService;
        Theme.Apply(this); Title = "Mở quản trị — DriveLink · Quang Huy"; Icon = AppBrand.Icon; Width = 460;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "Mật khẩu quản trị", FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"Nhập mật khẩu quản trị để mở giao diện. Sai {AdminLockout.AttemptsPerRound} lần phải chờ 15 phút; sai tổng cộng {AdminLockout.MaximumAttempts} lần thì máy này bị khóa mở quản trị.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 14), Foreground = Theme.Muted });
        PasswordFeedback.Add(panel, password); submit.Style = (Style)FindResource("Primary"); submit.Margin = new(0, 14, 0, 0); panel.Children.Add(submit); panel.Children.Add(status);
        Content = panel; Loaded += (_, _) => { if (!ApplyLockout()) password.Focus(); };
        submit.Click += async (_, _) => await SubmitAsync();
        Closing += (_, e) => { if (busy) { lifetime.Cancel(); e.Cancel = true; status.Text = "Đang hủy xác thực…"; } };
        Closed += (_, _) => { lifetime.Cancel(); candidate?.Dispose(); password.Clear(); };
    }
    /// <summary>Khóa ô nhập khi máy đang phải chờ hoặc đã bị khóa hẳn. Trả về true nếu đang bị chặn.</summary>
    private bool ApplyLockout()
    {
        var (allowed, message) = lockout.Check();
        if (allowed) return false;
        status.Foreground = Theme.Danger; status.Text = "⚠ " + message;
        submit.IsEnabled = password.IsEnabled = false;
        return true;
    }
    private async Task SubmitAsync()
    {
        if (busy || ApplyLockout()) return;
        if (!PasswordFeedback.Required(password, "Nhập mật khẩu quản trị.")) return;
        busy = true; submit.IsEnabled = password.IsEnabled = false; status.Foreground = Theme.Ink; status.Text = "Đang xác thực quản trị…";
        var secret = password.Password; password.Clear();
        var blocked = false;
        try
        {
            candidate = createService();
            // Cloud chặn theo địa chỉ IP (sau khi chạy supabase/upgrade-1.2.0.sql); Cloud chưa nâng cấp thì chỉ áp khóa trên máy.
            if (await candidate.GetAdminGateAsync(lifetime.Token) is { } gate && (gate.Blocked || gate.CooldownUntil > DateTimeOffset.UtcNow))
            {
                blocked = true;
                if (gate.Blocked) { lockout.Lock("Cloud đã chặn địa chỉ IP của máy này sau quá nhiều lần nhập sai"); Failure = "Cloud đã chặn mở quản trị từ địa chỉ mạng này sau quá nhiều lần nhập sai mật khẩu."; }
                else { status.Foreground = Theme.Danger; status.Text = $"⚠ Cloud yêu cầu chờ tới {gate.CooldownUntil!.Value.ToLocalTime():HH:mm} rồi mới thử lại."; }
            }
            if (!blocked)
            {
                await candidate.LoginAsync(CloudDefaults.AdminEmail, secret, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                if (!candidate.IsAdmin) { Failure = "Tài khoản chưa được cấp quyền quản trị."; }
                else
                {
                    lockout.RecordSuccess();
                    try { await candidate.ReportAdminSuccessAsync(lifetime.Token); } catch (HttpRequestException) { }
                    SecurityLog.Write("admin-signed-in", "Mở quản trị Cloud thành công");
                    Session = candidate; candidate = null;
                }
            }
        }
        catch (CloudAuthenticationException)
        {
            var reminder = lockout.RecordFailure();
            try { if (await candidate!.ReportAdminFailureAsync(lifetime.Token) is { Blocked: true }) lockout.Lock("Cloud đã chặn địa chỉ IP của máy này sau quá nhiều lần nhập sai"); } catch (HttpRequestException) { }
            Failure = "Nhập sai mật khẩu quản trị. " + reminder;
        }
        catch (OperationCanceledException) { Failure = "Đã hủy mở quản trị."; }
        catch (HttpRequestException) { status.Text = "Không kết nối được Cloud. Kiểm tra mạng rồi thử lại."; }
        catch (Exception ex) { Failure = ex.Message; }
        finally { busy = false; submit.IsEnabled = password.IsEnabled = true; }
        if (Session is not null || Failure.Length > 0) DialogResult = Session is not null;
        else { candidate?.Dispose(); candidate = null; ApplyLockout(); }
    }
}
