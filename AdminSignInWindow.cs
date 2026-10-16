using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DriveLink.Services;
namespace DriveLink;

public sealed class AdminSignInWindow : Window
{
    private readonly PasswordBox password = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly Button submit = new() { Content = "Mở quản trị", IsDefault = true };
    private readonly CancellationTokenSource lifetime = new();
    private SupabaseConfigService? candidate;
    private readonly Func<SupabaseConfigService> createService;
    private bool busy;
    public SupabaseConfigService? Session { get; private set; }
    public string Failure { get; private set; } = "";
    public AdminSignInWindow(Func<SupabaseConfigService> createService)
    {
        this.createService = createService;
        Title = "Mở quản trị — DriveLink · Quang Huy"; Icon = AppBrand.Icon; Width = 460;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "Mật khẩu quản trị", FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Nhập mật khẩu quản trị để mở giao diện. Tài khoản quản trị đã được thiết lập sẵn.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 14), Foreground = Brushes.SlateGray });
        PasswordFeedback.Add(panel, password); submit.Style = (Style)FindResource("Primary"); submit.Margin = new(0, 14, 0, 0); panel.Children.Add(submit); panel.Children.Add(status);
        Content = panel; Loaded += (_, _) => password.Focus();
        submit.Click += async (_, _) => await SubmitAsync();
        Closing += (_, e) => { if (busy) { lifetime.Cancel(); e.Cancel = true; status.Text = "Đang hủy xác thực…"; } };
        Closed += (_, _) => { lifetime.Cancel(); candidate?.Dispose(); password.Clear(); };
    }
    private async Task SubmitAsync()
    {
        if (busy) return;
        if (!PasswordFeedback.Required(password, "Nhập mật khẩu quản trị.")) return;
        busy = true; submit.IsEnabled = password.IsEnabled = false; status.Foreground = Brushes.Black; status.Text = "Đang xác thực quản trị…";
        var secret = password.Password; password.Clear();
        try
        {
            candidate = createService();
            await candidate.LoginAsync(CloudDefaults.AdminEmail, secret, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (!candidate.IsAdmin) { Failure = "Tài khoản chưa được cấp quyền quản trị."; }
            else { Session = candidate; candidate = null; }
        }
        catch (CloudAuthenticationException) { Failure = "Nhập sai mật khẩu quản trị."; }
        catch (OperationCanceledException) { Failure = "Đã hủy mở quản trị."; }
        catch (HttpRequestException) { status.Text = "Không kết nối được Cloud. Kiểm tra mạng rồi thử lại."; }
        catch (Exception ex) { Failure = ex.Message; }
        finally { busy = false; submit.IsEnabled = password.IsEnabled = true; }
        if (Session is not null || Failure.Length > 0) DialogResult = Session is not null;
        else { candidate?.Dispose(); candidate = null; }
    }
}
