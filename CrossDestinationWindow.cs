using System.Windows;
using System.Windows.Controls;
using DriveLink.Services;
using DriveLink.Ui;
namespace DriveLink;

/// <summary>Chọn nơi nhận khi gửi chéo: Drive của tôi hoặc một không gian [DriveLink] của tài khoản đích.</summary>
public sealed class CrossDestinationWindow : Window
{
    public sealed record Choice(string FolderId, string Label)
    {
        public override string ToString() => Label;
    }
    private readonly ListBox list = new() { Margin = new(0, 12, 0, 12), MinHeight = 160 };
    public Choice? Selected { get; private set; }

    public CrossDestinationWindow(string targetEmail, string summary, IReadOnlyList<Space> spaces, string? lastFolderId)
    {
        Theme.Apply(this);
        Title = "Chọn nơi nhận — Gửi chéo"; Width = 520; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var choices = new List<Choice> { new("root", "Drive của tôi (thư mục gốc)") };
        choices.AddRange(spaces.Select(s => new Choice(s.Id, "Không gian “" + s + "”")));
        list.ItemsSource = choices;
        list.SelectedItem = choices.FirstOrDefault(c => c.FolderId == lastFolderId) ?? choices[0];
        list.MouseDoubleClick += (_, _) => Accept();
        var ok = new Button { Content = "Gửi vào đây", IsDefault = true, Padding = new(18, 6, 18, 6), Margin = new(0, 0, 8, 0), Style = (Style)Application.Current.TryFindResource("PrimaryButton") };
        var cancel = new Button { Content = "Hủy", IsCancel = true, Padding = new(18, 6, 18, 6) };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(new TextBlock { Text = "Gửi chéo đến " + DisplayPrivacy.MaskEmail(targetEmail), FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = summary, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) });
        panel.Children.Add(list);
        panel.Children.Add(new TextBlock { Text = "Gửi vào không gian chung thì mọi thành viên của không gian đó đều thấy bản sao. Thư mục được giữ nguyên cấu trúc.", Foreground = Theme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) });
        panel.Children.Add(buttons);
        Content = panel;
    }

    private void Accept()
    {
        if (list.SelectedItem is not Choice choice) return;
        Selected = choice; DialogResult = true;
    }
}
