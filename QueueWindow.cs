using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using DriveLink.Services;
using DriveLink.Ui;
namespace DriveLink;

/// <summary>
/// Màn Hàng đợi &amp; Hoạt động: xem từng lượt gửi/nhận đang chạy hoặc đang chờ (hủy riêng từng lượt),
/// và nhật ký các lượt đã xong/lỗi/hủy (mở thư mục chứa tệp trên máy). Không hiển thị email đầy đủ.
/// </summary>
public sealed class QueueWindow : Window
{
    private readonly TransferCenter center;
    private readonly ListBox queue = new() { BorderThickness = new(0), Background = System.Windows.Media.Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox history = new() { BorderThickness = new(0), Background = System.Windows.Media.Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock queueEmpty = new() { Text = "Không có lượt gửi/nhận nào đang chạy hoặc đang chờ.", Foreground = Theme.Muted, Margin = new(4, 12, 4, 0) };
    private readonly TextBlock historyEmpty = new() { Text = "Chưa có hoạt động nào.", Foreground = Theme.Muted, Margin = new(4, 12, 4, 0) };
    private readonly TextBlock limits = new() { Foreground = Theme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    private const string QueueTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Border Padding="12,10" Margin="0,0,0,8" CornerRadius="12" Background="{DynamicResource P_12FFFFFF}" BorderBrush="{DynamicResource P_22FFFFFF}" BorderThickness="1">
            <Grid>
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <StackPanel>
                <TextBlock FontWeight="SemiBold" TextTrimming="CharacterEllipsis"><Run Text="{Binding DirectionLabel, Mode=OneWay}" Foreground="{DynamicResource P_FF9DB8FF}"/><Run Text=" · "/><Run Text="{Binding Name, Mode=OneWay}"/></TextBlock>
                <TextBlock FontSize="12" Foreground="{DynamicResource P_FFA6B0C4}" Margin="0,3,0,6" TextTrimming="CharacterEllipsis"><Run Text="{Binding StateLabel, Mode=OneWay}"/><Run Text=" · "/><Run Text="{Binding Space, Mode=OneWay}"/><Run Text=" · "/><Run Text="{Binding AmountLabel, Mode=OneWay}"/><Run Text="  "/><Run Text="{Binding SpeedLabel, Mode=OneWay}"/></TextBlock>
                <ProgressBar Height="6" Maximum="100" Value="{Binding Percent, Mode=OneWay}"/>
                <TextBlock FontSize="12" Foreground="{DynamicResource P_FFFBBF24}" Margin="0,4,0,0" TextWrapping="Wrap" Text="{Binding Note, Mode=OneWay}"/>
              </StackPanel>
              <Button Grid.Column="1" Content="Hủy" Margin="12,0,0,0" Padding="12,4" VerticalAlignment="Center" Tag="{Binding}" IsEnabled="{Binding CanCancel, Mode=OneWay}"/>
            </Grid>
          </Border>
        </DataTemplate>
        """;

    private const string HistoryTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Border Padding="12,8" Margin="0,0,0,6" CornerRadius="10" Background="{DynamicResource P_0EFFFFFF}">
            <Grid>
              <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <TextBlock Text="{Binding TimeLabel}" Foreground="{DynamicResource P_FFA6B0C4}" FontSize="12" Width="80" VerticalAlignment="Center"/>
              <StackPanel Grid.Column="1">
                <TextBlock TextTrimming="CharacterEllipsis"><Run Text="{Binding DirectionLabel, Mode=OneWay}" Foreground="{DynamicResource P_FF9DB8FF}"/><Run Text=" · "/><Run Text="{Binding Name, Mode=OneWay}"/></TextBlock>
                <TextBlock FontSize="12" Foreground="{DynamicResource P_FFA6B0C4}" TextTrimming="CharacterEllipsis"><Run Text="{Binding ResultLabel, Mode=OneWay}"/><Run Text=" · "/><Run Text="{Binding Space, Mode=OneWay}"/><Run Text=" · "/><Run Text="{Binding SizeLabel, Mode=OneWay}"/><Run Text=" · "/><Run Text="{Binding Note, Mode=OneWay}"/></TextBlock>
              </StackPanel>
              <Button Grid.Column="2" Content="Mở thư mục" Margin="10,0,0,0" Padding="10,3" VerticalAlignment="Center" Tag="{Binding}"/>
            </Grid>
          </Border>
        </DataTemplate>
        """;

    public QueueWindow(TransferCenter center)
    {
        this.center = center;
        Theme.Apply(this);
        Title = "Hàng đợi & Hoạt động — DriveLink"; Width = 720; Height = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        queue.ItemTemplate = (DataTemplate)XamlReader.Parse(QueueTemplate);
        history.ItemTemplate = (DataTemplate)XamlReader.Parse(HistoryTemplate);
        queue.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is Button { Tag: TransferItem item }) center.Cancel(item); }));
        history.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is Button { Tag: ActivityEntry entry }) OpenFolder(entry); }));

        var cancelUploads = new Button { Content = "Hủy mọi lượt gửi", Margin = new(0, 0, 8, 0) };
        var cancelDownloads = new Button { Content = "Hủy mọi lượt nhận" };
        cancelUploads.Click += (_, _) => center.CancelAll(TransferDirection.Upload);
        cancelDownloads.Click += (_, _) => center.CancelAll(TransferDirection.Download);
        var queuePage = new DockPanel { Margin = new(20) };
        var queueHeader = new StackPanel();
        queueHeader.Children.Add(limits);
        var queueButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 12) };
        queueButtons.Children.Add(cancelUploads); queueButtons.Children.Add(cancelDownloads);
        queueHeader.Children.Add(queueButtons);
        DockPanel.SetDock(queueHeader, Dock.Top);
        queuePage.Children.Add(queueHeader);
        queuePage.Children.Add(new Grid { Children = { queueEmpty, queue } });

        var clear = new Button { Content = "Xóa nhật ký hoạt động", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 12) };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Xóa toàn bộ nhật ký hoạt động trên máy này? Tệp trên Drive và trên máy không bị ảnh hưởng.", "Xóa nhật ký", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { center.ClearActivity(); Render(); }
        };
        var historyPage = new DockPanel { Margin = new(20) };
        var historyHeader = new StackPanel();
        historyHeader.Children.Add(new TextBlock { Text = $"{TransferCenter.ActivityLimit} lượt gần nhất, lưu mã hóa trên máy này.", Foreground = Theme.Muted, FontSize = 12, Margin = new(0, 0, 0, 10) });
        historyHeader.Children.Add(clear);
        DockPanel.SetDock(historyHeader, Dock.Top);
        historyPage.Children.Add(historyHeader);
        historyPage.Children.Add(new Grid { Children = { historyEmpty, history } });

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Hàng đợi", Content = queuePage });
        tabs.Items.Add(new TabItem { Header = "Hoạt động", Content = historyPage });
        Content = tabs;

        center.Changed += OnChanged;
        refresh.Tick += (_, _) => RenderLimits();
        Loaded += (_, _) => { Render(); refresh.Start(); };
        Closed += (_, _) => { center.Changed -= OnChanged; refresh.Stop(); };
    }

    public void ShowActivity() { if (Content is TabControl tabs) tabs.SelectedIndex = 1; }

    private void OnChanged() => Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        var active = center.Items.Where(i => i.IsActive).ToList();
        if (!active.SequenceEqual(queue.ItemsSource as IEnumerable<TransferItem> ?? [])) queue.ItemsSource = active;
        queueEmpty.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var entries = center.Activity;
        history.ItemsSource = entries;
        historyEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderLimits();
    }

    private void RenderLimits()
    {
        var up = center.Summary(TransferDirection.Upload); var down = center.Summary(TransferDirection.Download);
        limits.Text = $"Gửi: {up.Active} đang chạy, {up.Waiting} chờ (tối đa {center.UploadLimit.Current} tệp cùng lúc) · Nhận: {down.Active} đang chạy, {down.Waiting} chờ (tối đa {center.DownloadLimit.Current}). " +
                      "Số tệp song song tự điều chỉnh 2–6 theo tốc độ mạng; mạng chập chờn thì tự thử lại và giảm bớt.";
    }

    private void OpenFolder(ActivityEntry entry)
    {
        var path = entry.LocalPath;
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path))) { MessageBox.Show(this, "Không còn tìm thấy tệp này trên máy (đã bị xóa hoặc di chuyển).", "Mở thư mục"); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Mở thư mục"); }
    }
}
