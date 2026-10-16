using System.Reflection;
using System.Windows.Media.Imaging;
namespace DriveLink;

public static class AppBrand
{
    public const string Name = "DriveLink";
    public const string Author = "Quang Huy";
    public static string Version => typeof(AppBrand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.3.0";
    public static string WindowTitle => $"{Name} — {Author} · v{Version}";
    public static string Credit => $"{Author} · v{Version}";
    private static readonly Lazy<BitmapImage> image = new(() =>
    {
        using var source = typeof(AppBrand).Assembly.GetManifestResourceStream("DriveLink.BrandIcon") ?? throw new InvalidOperationException("Thiếu icon DriveLink.");
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = source; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    });
    private static readonly Lazy<string> webIcon = new(() =>
    {
        using var source = typeof(AppBrand).Assembly.GetManifestResourceStream("DriveLink.WebIcon") ?? throw new InvalidOperationException("Thiếu icon web DriveLink.");
        using var data = new MemoryStream(); source.CopyTo(data);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    });
    public static BitmapImage Icon => image.Value;
    public static string WebIconDataUri => webIcon.Value;
}
