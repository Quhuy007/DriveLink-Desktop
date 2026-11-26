using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DriveLink.Ui;
namespace DriveLink;

/// <summary>
/// Sắp xếp bố cục bento: bấm nút Sắp xếp ở thanh bên, kéo một ô thả lên ô khác để đổi chỗ, đổi cỡ thường/cao, ẩn/hiện,
/// chuyển danh sách tệp sang trái/phải. Thay đổi hiện ngay; bấm Xong (hoặc Esc) để lưu, Hủy để bỏ.
/// </summary>
public partial class MainWindow
{
    private const string BentoFormat = "DriveLink.BentoTile";
    /// <summary>Bố cục đang sửa; null khi không ở chế độ sắp xếp.</summary>
    private BentoLayout? arranging;
    private readonly List<FrameworkElement> arrangeOverlays = [];
    private Border? arrangeBar;
    private Point dragStart;

    private IReadOnlyDictionary<string, Ui.GlassCard> BentoCards => new Dictionary<string, Ui.GlassCard>
    {
        [BentoLayout.Send] = SendCard, [BentoLayout.Transfer] = TransferCard, [BentoLayout.Spaces] = SpacesCard,
        [BentoLayout.Cross] = CrossCard, [BentoLayout.Storage] = StorageCard,
    };

    private BentoLayout CurrentLayout => arranging ?? (state.Settings.Layout ?? BentoLayout.Default()).Normalize();
    private bool IsArranging => arranging is not null;

    /// <summary>Đặt các ô vào lưới theo bố cục người dùng; gọi từ ApplyLayout mỗi khi cửa sổ đổi cỡ hoặc bố cục đổi.</summary>
    private void PlaceBento(bool wide)
    {
        var layout = CurrentLayout;
        var (placements, rows) = layout.Pack(wide ? 2 : 1);
        SideGrid.RowDefinitions.Clear();
        for (var i = 0; i < Math.Max(1, rows); i++) SideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        foreach (var (id, card) in BentoCards)
        {
            if (placements.FirstOrDefault(p => p.Id == id) is { Id.Length: > 0 } p)
            {
                Grid.SetRow(card, p.Row); Grid.SetColumn(card, p.Column); Grid.SetRowSpan(card, p.RowSpan); Grid.SetColumnSpan(card, p.ColumnSpan);
                card.Visibility = Visibility.Visible;
            }
            else card.Visibility = Visibility.Collapsed;
        }
        // Mỗi hàng giữ tối thiểu ~200 px; nhiều ô hơn chỗ thì cột ô nhỏ cuộn được.
        SideGrid.MinHeight = Math.Max(1, rows) * (wide ? 200 : 213);
        SideScroll.Visibility = rows == 0 ? Visibility.Collapsed : Visibility.Visible;
        var right = layout.FilesOnRight;
        Bento.ColumnDefinitions[0].Width = right ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        Bento.ColumnDefinitions[1].Width = right ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetColumn(FilesCard, right ? 1 : 0); Grid.SetColumn(SideScroll, right ? 0 : 1);
        if (IsArranging) BuildArrangeOverlays(placements);
    }

    private void Arrange_Click(object sender, RoutedEventArgs e) { if (IsArranging) FinishArrange(save: true); else StartArrange(); }

    private void StartArrange()
    {
        if (IsArranging) return;
        arranging = (state.Settings.Layout ?? BentoLayout.Default()).Normalize();
        arrangeBar = new Border { Child = new StackPanel(), Margin = new Thickness(0, 0, 0, 22) };
        Panel.SetZIndex(arrangeBar, 25);
        arrangeBar.HorizontalAlignment = HorizontalAlignment.Center; arrangeBar.VerticalAlignment = VerticalAlignment.Bottom;
        WindowRoot.Children.Add(arrangeBar);
        ApplyZoom();
    }

    private void FinishArrange(bool save)
    {
        if (arranging is not { } edited) return;
        arranging = null;
        if (save)
        {
            state.Settings.Layout = edited.Normalize();
            try { store.Save(state); StatusLabel.Text = edited.IsDefault ? "Đã dùng bố cục mặc định." : "Đã lưu bố cục mới."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { StatusLabel.Text = "Chưa lưu được bố cục: " + ex.Message; }
        }
        ClearArrangeOverlays();
        if (arrangeBar is not null) { WindowRoot.Children.Remove(arrangeBar); arrangeBar = null; }
        ApplyZoom();
    }

    private void ChangeArrange(Action<BentoLayout> change)
    {
        if (arranging is null) return;
        change(arranging);
        ApplyZoom();
    }

    private void ClearArrangeOverlays()
    {
        foreach (var overlay in arrangeOverlays) (overlay.Parent as Panel)?.Children.Remove(overlay);
        arrangeOverlays.Clear();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void BuildArrangeOverlays(IReadOnlyList<BentoPlacement> placements)
    {
        ClearArrangeOverlays();
        var layout = arranging!;
        foreach (var p in placements)
        {
            var tile = layout[p.Id];
            var size = ArrangeButton(tile.Tall ? "Cỡ: cao" : "Cỡ: thường", "Đổi giữa ô thường (1 hàng) và ô cao (2 hàng)", () => ChangeArrange(l => l[p.Id].Tall = !l[p.Id].Tall));
            var hide = ArrangeButton("Ẩn", "Ẩn ô này (hiện lại từ thanh Sắp xếp bên dưới)", () => ChangeArrange(l => l[p.Id].Visible = false));
            var overlay = ArrangeOverlay(BentoLayout.Title(p.Id), "Kéo thả lên ô khác để đổi chỗ", p.Id, [size, hide]);
            Grid.SetRow(overlay, p.Row); Grid.SetColumn(overlay, p.Column); Grid.SetRowSpan(overlay, p.RowSpan); Grid.SetColumnSpan(overlay, p.ColumnSpan);
            SideGrid.Children.Add(overlay); arrangeOverlays.Add(overlay);
        }
        var side = ArrangeButton(layout.FilesOnRight ? "⇄ Chuyển sang trái" : "⇄ Chuyển sang phải", "Đổi bên cho danh sách tệp và cột ô nhỏ", () => ChangeArrange(l => l.FilesOnRight = !l.FilesOnRight));
        var files = ArrangeOverlay("Danh sách tệp", "Vùng lớn, luôn hiện", null, [side]);
        Grid.SetColumn(files, Grid.GetColumn(FilesCard));
        Bento.Children.Add(files); arrangeOverlays.Add(files);
        BuildArrangeBar(layout);
    }

    private Button ArrangeButton(string text, string tip, Action click, bool primary = false)
    {
        var button = new Button { Content = text, ToolTip = tip, Height = 30, Padding = new Thickness(12, 0, 12, 0), FontSize = 12, Margin = new Thickness(6, 0, 0, 0), Style = (Style)FindResource(primary ? "GlPrimaryButton" : "GlButton") };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Lớp phủ trên một ô khi sắp xếp: viền nét đứt, tên ô, nút; kéo được (ô nhỏ) và nhận thả.</summary>
    private Grid ArrangeOverlay(string title, string hint, string? id, Button[] buttons)
    {
        var fill = new Border { CornerRadius = new CornerRadius(26), Background = Res("P_F00C111D") };
        var dash = new Rectangle { RadiusX = 26, RadiusY = 26, Stroke = Res("GlAccent"), StrokeThickness = 2, StrokeDashArray = [5, 4], IsHitTestVisible = false };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12, 40, 12, 12), IsHitTestVisible = false };
        if (id is not null) text.Children.Add(new TextBlock { Text = "⠿", FontSize = 24, Foreground = Res("GlMuted"), HorizontalAlignment = HorizontalAlignment.Center });
        text.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("GlInk"), HorizontalAlignment = HorizontalAlignment.Center });
        text.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = Res("GlMuted"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 14, 0) };
        foreach (var b in buttons) actions.Children.Add(b);
        var root = new Grid { Margin = new Thickness(8), Background = Brushes.Transparent, Cursor = id is null ? Cursors.Arrow : Cursors.SizeAll, Tag = id };
        root.Children.Add(fill); root.Children.Add(dash); root.Children.Add(text); root.Children.Add(actions);
        Panel.SetZIndex(root, 10);
        if (id is null) return root;
        AutomationName(root, "Ô " + title + ", kéo để đổi chỗ");
        root.MouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(this);
        root.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;
            var delta = e.GetPosition(this) - dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            root.Opacity = 0.45;
            try { DragDrop.DoDragDrop(root, new DataObject(BentoFormat, id), DragDropEffects.Move); }
            finally { root.Opacity = 1; }
        };
        root.AllowDrop = true;
        void Mark(bool on) { dash.StrokeThickness = on ? 4 : 2; fill.Background = on ? Res("GlAccentFill") : Res("P_F00C111D"); }
        root.DragEnter += (_, e) => { if (Accepts(e, id)) Mark(true); };
        root.DragOver += (_, e) => { e.Effects = Accepts(e, id) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        root.DragLeave += (_, _) => Mark(false);
        root.Drop += (_, e) =>
        {
            e.Handled = true; Mark(false);
            if (Accepts(e, id) && e.Data.GetData(BentoFormat) is string from) ChangeArrange(l => l.Swap(from, id));
        };
        return root;
    }

    private static bool Accepts(DragEventArgs e, string target) => e.Data.GetDataPresent(BentoFormat) && e.Data.GetData(BentoFormat) is string from && from != target;

    private static void AutomationName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T) node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    /// <summary>Thanh nổi ở đáy cửa sổ khi sắp xếp: hướng dẫn, các ô đang ẩn (bấm để hiện lại), Mặc định / Hủy / Xong.</summary>
    private void BuildArrangeBar(BentoLayout layout)
    {
        if (arrangeBar is null) return;
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
        label.Children.Add(new TextBlock { Text = "Sắp xếp bố cục", FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = Res("GlInk") });
        label.Children.Add(new TextBlock { Text = "Kéo ô để đổi chỗ · Esc hoặc Xong để lưu", FontSize = 11.5, Foreground = Res("GlMuted") });
        row.Children.Add(label);
        foreach (var hidden in layout.Hidden.ToList())
            row.Children.Add(ArrangeButton("＋ " + BentoLayout.Title(hidden.Id), "Hiện lại ô này", () => ChangeArrange(l => l[hidden.Id].Visible = true)));
        var reset = ArrangeButton("Mặc định", "Đưa về bố cục ban đầu", () => ChangeArrange(l => { var d = BentoLayout.Default(); l.Tiles = d.Tiles; l.FilesOnRight = false; }));
        reset.IsEnabled = !layout.IsDefault;
        row.Children.Add(reset);
        row.Children.Add(ArrangeButton("Hủy", "Bỏ các thay đổi chưa lưu", () => FinishArrange(save: false)));
        row.Children.Add(ArrangeButton("Xong", "Lưu bố cục", () => FinishArrange(save: true), primary: true));
        arrangeBar.Child = row;
        arrangeBar.Padding = new Thickness(14, 10, 12, 10);
        arrangeBar.CornerRadius = new CornerRadius(24);
        arrangeBar.Background = Res("P_F00C111D");
        arrangeBar.BorderBrush = Res("GlRim"); arrangeBar.BorderThickness = new Thickness(1);
        arrangeBar.MaxWidth = Math.Max(320, ActualWidth - 120);
    }
}
