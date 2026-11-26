using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace DriveLink.Ui;

/// <summary>Thẻ kính của lưới Bento: nổi nhẹ khi rê chuột, có vệt sáng đi theo con trỏ và hiệu ứng xuất hiện so le.</summary>
public sealed class GlassCard : ContentControl
{
    public static readonly DependencyProperty IsHighlightedProperty =
        DependencyProperty.Register(nameof(IsHighlighted), typeof(bool), typeof(GlassCard), new PropertyMetadata(false));
    public bool IsHighlighted { get => (bool)GetValue(IsHighlightedProperty); set => SetValue(IsHighlightedProperty, value); }

    /// <summary>Độ nổi khi rê chuột (px). Thẻ lớn như danh sách tệp đặt 0 để không bị rung khi làm việc.</summary>
    public static readonly DependencyProperty HoverLiftProperty =
        DependencyProperty.Register(nameof(HoverLift), typeof(double), typeof(GlassCard), new PropertyMetadata(4d));
    public double HoverLift { get => (double)GetValue(HoverLiftProperty); set => SetValue(HoverLiftProperty, value); }

    /// <summary>Tắt khi Windows bật "giảm hiệu ứng chuyển động" để tôn trọng lựa chọn của người dùng.</summary>
    public static bool Motion { get; set; } = true;

    private readonly ScaleTransform scale = new(1, 1);
    private readonly TranslateTransform enter = new();
    private readonly TranslateTransform lift = new();
    private readonly RadialGradientBrush spot = new() { MappingMode = BrushMappingMode.Absolute, RadiusX = 240, RadiusY = 240 };
    private Border? spotLayer;

    public GlassCard()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new TransformGroup { Children = { scale, enter, lift } };
        spot.GradientStops.Add(new GradientStop(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), 0));
        spot.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        spotLayer = GetTemplateChild("PART_Spot") as Border;
        if (spotLayer is not null) spotLayer.Background = spot;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (Motion && HoverLift > 0) lift.BeginAnimation(TranslateTransform.YProperty, Ease(-HoverLift, 240));
        spotLayer?.BeginAnimation(OpacityProperty, Ease(1, 240));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        lift.BeginAnimation(TranslateTransform.YProperty, Ease(0, 360));
        spotLayer?.BeginAnimation(OpacityProperty, Ease(0, 360));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        spot.Center = point;
        spot.GradientOrigin = point;
    }

    public void PlayEntrance(TimeSpan delay, double distance = 18)
    {
        if (!Motion) { BeginAnimation(OpacityProperty, null); Opacity = 1; return; }
        var start = 1 - distance / 300;
        Opacity = 0;
        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(start, 1, TimeSpan.FromMilliseconds(560)) { BeginTime = delay, EasingFunction = back });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(start, 1, TimeSpan.FromMilliseconds(560)) { BeginTime = delay, EasingFunction = back });
        enter.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(560)) { BeginTime = delay, EasingFunction = back });
    }

    private static DoubleAnimation Ease(double to, int ms) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
}
