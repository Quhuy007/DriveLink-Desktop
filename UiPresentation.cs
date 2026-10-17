using System.Windows;
using System.Windows.Media;
namespace DriveLink;
public static class UiPresentation
{
    public static double Scale { get; set; } = 1;
    public static void Fit(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MaxWidth = area.Width; window.MaxHeight = area.Height;
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        if (!double.IsNaN(window.Height)) window.Height = Math.Min(window.Height, area.Height);
        if (!double.IsNaN(window.Width)) window.Width = Math.Min(window.Width, area.Width);
        if (window is not MainWindow && window.Content is FrameworkElement root) root.LayoutTransform = new ScaleTransform(Scale, Scale);
    }
}
