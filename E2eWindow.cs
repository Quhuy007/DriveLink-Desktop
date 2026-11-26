using System.Windows;
using System.Windows.Controls;
using DriveLink.Services;
using DriveLink.Ui;
namespace DriveLink;

/// <summary>
/// Bật/tắt mã hóa đầu cuối cho một không gian trên máy này: nhập mật khẩu không gian (mọi thành viên nhập cùng một mật khẩu),
/// xem mã kiểm tra để so với nhau, hoặc xóa khóa khỏi máy. Mật khẩu không được lưu; chỉ khóa dẫn xuất được lưu mã hóa DPAPI.
/// </summary>
public sealed class E2eWindow : Window
{
    private readonly PasswordBox first = new() { Margin = new(0, 4, 0, 8) };
    private readonly PasswordBox second = new() { Margin = new(0, 4, 0, 8) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 10) };
    public bool Changed { get; private set; }

    public E2eWindow(E2eKeys keys, string spaceId, string spaceName, bool askOnly = false)
    {
        Theme.Apply(this);
        Title = "Mã hóa đầu cuối — " + spaceName; Width = 560; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var existing = keys.Find(spaceId);
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "Mã hóa đầu cuối · " + spaceName, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = (askOnly ? "Tệp này được mã hóa đầu cuối. Nhập mật khẩu của không gian để giải mã.\n\n" : "") +
                   "Khi bật, tệp gửi vào không gian này được mã hóa trên máy bạn trước khi tải lên (AES-256); Google và người không có mật khẩu chỉ thấy dữ liệu đã mã hóa. " +
                   "Mọi thành viên cần nhập CÙNG một mật khẩu trên máy của họ. Mất mật khẩu thì không ai mở được các tệp đã mã hóa. Tên tệp vẫn thấy được trên Drive.",
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new(0, 8, 0, 14),
        });
        if (existing is not null)
            panel.Children.Add(new TextBlock { Text = $"Đang bật trên máy này · mã kiểm tra {existing.Fingerprint}", Foreground = Theme.Success, Margin = new(0, 0, 0, 12), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Mật khẩu không gian (ít nhất 10 ký tự)" });
        panel.Children.Add(first);
        if (!askOnly) { panel.Children.Add(new TextBlock { Text = "Nhập lại mật khẩu" }); panel.Children.Add(second); }
        panel.Children.Add(status);
        var save = new Button { Content = existing is null ? (askOnly ? "Mở khóa" : "Bật mã hóa") : "Đổi mật khẩu trên máy này", IsDefault = true, Padding = new(16, 6, 16, 6), Margin = new(0, 0, 8, 0) };
        var remove = new Button { Content = "Tắt trên máy này", Padding = new(16, 6, 16, 6), Margin = new(0, 0, 8, 0), Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible };
        var close = new Button { Content = "Đóng", IsCancel = true, Padding = new(16, 6, 16, 6) };
        save.Click += (_, _) =>
        {
            var pass = first.Password;
            if (pass.Length < 10) { status.Text = "Mật khẩu cần ít nhất 10 ký tự."; status.Foreground = Theme.Danger; first.Focus(); return; }
            if (!askOnly && pass != second.Password) { status.Text = "Hai lần nhập không khớp."; status.Foreground = Theme.Danger; second.Focus(); return; }
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                var entry = keys.Set(spaceId, spaceName, pass);
                SecurityLog.Write("e2e-key-set", $"Đặt khóa mã hóa đầu cuối cho không gian {spaceId} (mã kiểm tra {entry.Fingerprint})");
                Changed = true;
                MessageBox.Show(this, $"Đã bật mã hóa đầu cuối cho “{spaceName}” trên máy này.\n\nMã kiểm tra: {entry.Fingerprint}\nCác thành viên khác nhập cùng mật khẩu sẽ thấy cùng mã này.", "Mã hóa đầu cuối", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            finally { Cursor = null; first.Clear(); second.Clear(); }
        };
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Xóa khóa của không gian này khỏi máy? Tệp mới sẽ gửi không mã hóa, và máy này không mở được tệp đã mã hóa cho tới khi nhập lại mật khẩu.", "Tắt mã hóa", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            keys.Remove(spaceId);
            SecurityLog.Write("e2e-key-removed", "Xóa khóa mã hóa đầu cuối của không gian " + spaceId);
            Changed = true; DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save); buttons.Children.Add(remove); buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => first.Focus();
    }
}
