using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.Text;
using WebRtcDemo.Core.Media;

namespace WebRtcDemo.App.Ui;

/// <summary>Small builders that keep the code-only layout readable.</summary>
internal static class UiFactory
{
    public const string IconFont = "Segoe Fluent Icons,Segoe MDL2 Assets";

    public static FontIcon Icon(string glyph, double size = 16) => new()
    {
        Glyph = glyph,
        FontSize = size,
        FontFamily = new FontFamily(IconFont),
    };

    public static TextBlock Text(string text, double size = 14, FontWeight? weight = null, Brush? foreground = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (weight is { } w) block.FontWeight = w;
        if (foreground != null) block.Foreground = foreground;
        return block;
    }

    public static Color Rgb(uint rgb, byte alpha = 255) =>
        Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static SolidColorBrush Brush(uint rgb, byte alpha = 255) => new(Rgb(rgb, alpha));

    public static SolidColorBrush White(byte alpha = 255) => new(Color.FromArgb(alpha, 255, 255, 255));

    public static SolidColorBrush Black(byte alpha = 255) => new(Color.FromArgb(alpha, 0, 0, 0));

    public static readonly SolidColorBrush Transparent = new(Colors.Transparent);

    /// <summary>A brush from the theme dictionaries (follows the element's theme when set at load).</summary>
    public static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    public static T With<T>(this T element, Action<T> configure) where T : DependencyObject
    {
        configure(element);
        return element;
    }

    public static T Grid<T>(this T element, int row = 0, int column = 0, int rowSpan = 1, int columnSpan = 1) where T : FrameworkElement
    {
        Microsoft.UI.Xaml.Controls.Grid.SetRow(element, row);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(element, column);
        if (rowSpan > 1) Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(element, rowSpan);
        if (columnSpan > 1) Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(element, columnSpan);
        return element;
    }

    public static T Tip<T>(this T element, string tip) where T : DependencyObject
    {
        ToolTipService.SetToolTip(element, tip);
        return element;
    }

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Column(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static LinearGradientBrush Gradient(uint start, uint end) => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 1),
        GradientStops =
        {
            new GradientStop { Color = Rgb(start), Offset = 0 },
            new GradientStop { Color = Rgb(end), Offset = 1 },
        },
    };

    /// <summary>A BGRA image as a bitmap (alpha is opaque, so premultiplying changes nothing).</summary>
    public static WriteableBitmap ToBitmap(BgraImage image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(image.Pixels, 0, image.Pixels.Length);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    public static async Task<BitmapImage?> JpegAsync(byte[] jpeg)
    {
        if (jpeg.Length == 0) return null;
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
