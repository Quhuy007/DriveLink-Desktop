using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
namespace DriveLink.Ui;

/// <summary>Chọn biểu tượng và màu theo loại tệp. Tham số "brush" trả về màu, còn lại trả về ký tự biểu tượng.</summary>
public sealed class FileIconConverter : IValueConverter
{
    private static Brush Hex(string color) { var brush = (Brush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    private static readonly (string Glyph, Brush Brush) Folder = ("", Hex("#7EA6FF")), Image = ("", Hex("#2DD4BF")), Video = ("", Hex("#FB7185")),
        Audio = ("", Hex("#C084FC")), Archive = ("", Hex("#FBBF24")), Pdf = ("", Hex("#F87171")), Sheet = ("", Hex("#4ADE80")),
        Slides = ("", Hex("#FB923C")), Doc = ("", Hex("#A6B0C4"));

    public static (string Glyph, Brush Brush) Resolve(DriveItem item)
    {
        var mime = item.MimeType;
        if (mime == "application/vnd.google-apps.folder") return Folder;
        if (mime == "application/vnd.google-apps.spreadsheet") return Sheet;
        if (mime == "application/vnd.google-apps.presentation") return Slides;
        return Path.GetExtension(item.Name).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".heic" or ".bmp" or ".svg" => Image,
            ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" => Video,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" => Audio,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => Archive,
            ".pdf" => Pdf,
            ".xlsx" or ".xls" or ".csv" => Sheet,
            ".pptx" or ".ppt" => Slides,
            _ => mime.StartsWith("image/") ? Image : mime.StartsWith("video/") ? Video : mime.StartsWith("audio/") ? Audio : Doc,
        };
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DriveItem item) return null;
        var (glyph, brush) = Resolve(item);
        return parameter as string == "brush" ? brush : glyph;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
