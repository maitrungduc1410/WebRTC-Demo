using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Media;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>
/// Shown instead of the remote video when it is off or hidden: the last frame, tiny and blurred,
/// behind the peer's gradient avatar, whose ring pulses with their voice (as on iOS).
/// </summary>
internal sealed class RemotePlaceholder : Grid
{
    private const double AvatarSize = 112;

    private readonly Image _snapshot = new() { Stretch = Stretch.UniformToFill, Opacity = 0.85 };
    private readonly Ellipse _avatar = new() { Width = AvatarSize, Height = AvatarSize };
    private readonly Ellipse _ring = new()
    {
        Width = AvatarSize,
        Height = AvatarSize,
        Fill = White(0x40),
        Opacity = 0,
        CenterPoint = new Vector3((float)AvatarSize / 2, (float)AvatarSize / 2, 0),
        ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(200) },
        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) },
    };
    private readonly TextBlock _title = Text(string.Empty, 20, FontWeights.SemiBold, White());
    private readonly TextBlock _subtitle = Text(string.Empty, 14, foreground: White(0xB0));
    private readonly FontIcon _micOff = Icon(Glyphs.MicOff, 14);
    private string? _seed;

    public RemotePlaceholder()
    {
        Background = Brush(0x15151A);
        Children.Add(_snapshot);
        // Darkens the snapshot so the avatar and text stay readable on any frame.
        Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Rgb(0x000000, 0x66), Offset = 0 },
                    new GradientStop { Color = Rgb(0x000000, 0xAA), Offset = 1 },
                },
            },
        });

        var avatar = new Grid { Width = AvatarSize, Height = AvatarSize, HorizontalAlignment = HorizontalAlignment.Center };
        avatar.Children.Add(_ring);
        avatar.Children.Add(_avatar);
        avatar.Children.Add(Icon(Glyphs.Person, 44).With(i =>
        {
            i.Foreground = White();
            i.HorizontalAlignment = HorizontalAlignment.Center;
            i.VerticalAlignment = VerticalAlignment.Center;
        }));

        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.TextAlignment = TextAlignment.Center;
        _micOff.Foreground = White(0xB0);
        var subtitle = Row(6, _micOff, _subtitle).With(r => r.HorizontalAlignment = HorizontalAlignment.Center);
        Children.Add(Column(16, avatar, Column(4, _title, subtitle)).With(c =>
        {
            c.HorizontalAlignment = HorizontalAlignment.Center;
            c.VerticalAlignment = VerticalAlignment.Center;
        }));
    }

    public void SetSeed(string seed)
    {
        if (seed == _seed) return;
        _seed = seed;
        var (start, end) = Avatar.Colors(seed);
        _avatar.Fill = Gradient(start, end);
    }

    public void SetText(string title, string? subtitle)
    {
        _title.Text = title;
        _subtitle.Text = subtitle ?? string.Empty;
        _micOff.Visibility = Visible(subtitle != null);
    }

    public void SetSnapshot(BgraImage? image) => _snapshot.Source = image == null ? null : ToBitmap(image);

    /// <summary>Remote audio level 0..1; speech is quiet in these units, so it is amplified.</summary>
    public void SetLevel(double level)
    {
        var amount = (float)Math.Clamp(level * 3, 0, 1);
        _ring.Opacity = amount;
        var scale = 1 + 0.35f * amount;
        _ring.Scale = new Vector3(scale, scale, 1);
    }
}
