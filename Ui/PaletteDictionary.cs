using System.Windows;
namespace DriveLink;

/// <summary>Bảng màu của app: tối hoặc sáng theo <see cref="Ui.ThemeChoice"/>, nạp trước mọi kiểu dáng trong App.xaml.</summary>
public sealed class PaletteDictionary : ResourceDictionary
{
    public PaletteDictionary()
    {
        Source = new Uri(Ui.ThemeChoice.IsLight ? "/DriveLink;component/Ui/Palette.Light.xaml" : "/DriveLink;component/Ui/Palette.Dark.xaml", UriKind.Relative);
    }
}
