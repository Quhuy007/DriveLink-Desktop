using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
namespace DriveLink.Services;

public static class PasswordFeedback
{
    private static readonly ConditionalWeakTable<PasswordBox, TextBlock> errors = new();
    public static void Add(Panel panel, PasswordBox field)
    {
        field.Padding = new(12, 9, 12, 9); field.BorderThickness = new(1); field.BorderBrush = Brushes.SlateGray;
        // Keep the validation border visible even while the password input has focus.
        field.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type PasswordBox}">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6">
                    <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False" />
                </Border>
            </ControlTemplate>
            """);
        panel.Children.Add(field);
        var error = new TextBlock { Foreground = Brushes.Firebrick, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 2), Visibility = Visibility.Collapsed };
        errors.Add(field, error); panel.Children.Add(error);
        field.PasswordChanged += (_, _) => Clear(field);
    }
    public static void Clear(PasswordBox field)
    {
        field.BorderBrush = Brushes.SlateGray; field.BorderThickness = new(1); field.Background = Brushes.White;
        if (errors.TryGetValue(field, out var error)) { error.Text = ""; error.Visibility = Visibility.Collapsed; }
    }
    public static void Invalid(PasswordBox field, string message)
    {
        field.BorderBrush = Brushes.Firebrick; field.BorderThickness = new(2); field.Background = new SolidColorBrush(Color.FromRgb(255, 241, 242));
        if (errors.TryGetValue(field, out var error)) { error.Text = "⚠ " + message; error.Visibility = Visibility.Visible; }
        field.Focus();
    }
    public static bool Required(PasswordBox field, string message)
    {
        if (!string.IsNullOrEmpty(field.Password)) return true;
        Invalid(field, message); return false;
    }
}
