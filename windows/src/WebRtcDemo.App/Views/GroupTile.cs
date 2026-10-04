using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WebRtcDemo.App.Ui;
using WebRtcDemo.App.Video;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Group;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>
/// One participant of a group call (web: GroupTile.vue): their video, filled by default and fitted
/// while they present, or their avatar; a "name · id" label with mic-off or presenting icons, and
/// a green ring while they speak. A double-click switches fit and fill.
/// </summary>
internal sealed class GroupTile : Grid, IDisposable
{
    /// <summary>Tiles at least this tall say why the avatar shows.</summary>
    private const double RoomyHeight = 260;

    private readonly Bindings _bindings = new();
    private readonly VideoView _video = new() { AnimatesFit = true };
    private readonly Grid _placeholder = new() { Background = Brush(0x15151A) };
    private readonly Grid _avatar = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Ellipse _avatarFill = new();
    private readonly Ellipse _ring = new()
    {
        Fill = White(0x40),
        Opacity = 0,
        ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(200) },
        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) },
    };
    private readonly FontIcon _avatarIcon = Icon(Glyphs.Person, 28);
    private readonly TextBlock _placeholderTitle = Text(string.Empty, 14, FontWeights.SemiBold, White(0xD0));
    private readonly Border _speaking;
    private readonly StatusSlot _status = new();
    private readonly TextBlock _label = Text(string.Empty, 12, FontWeights.SemiBold, White()).With(t =>
    {
        t.TextWrapping = TextWrapping.NoWrap;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.VerticalAlignment = VerticalAlignment.Center;
    });

    public GroupTile(GroupParticipant participant)
    {
        Participant = participant;
        CornerRadius = new CornerRadius(16);
        Background = Brush(0x18181B);
        BorderBrush = White(0x1A);
        BorderThickness = new Thickness(1);
        // Fades in when added (and back when the stage shows it again after picture in picture).
        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(300) };

        Children.Add(_video);

        _avatar.Children.Add(_ring);
        _avatar.Children.Add(_avatarFill);
        _avatarIcon.Foreground = White();
        _avatarIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _avatarIcon.VerticalAlignment = VerticalAlignment.Center;
        _avatar.Children.Add(_avatarIcon);
        _placeholderTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _placeholder.Children.Add(Column(10, _avatar, _placeholderTitle).With(c =>
        {
            c.HorizontalAlignment = HorizontalAlignment.Center;
            c.VerticalAlignment = VerticalAlignment.Center;
        }));
        _placeholder.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(400) };
        Children.Add(_placeholder);

        _speaking = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderBrush = Brush(0x34D399),
            BorderThickness = new Thickness(3),
            Opacity = 0,
            IsHitTestVisible = false,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(300) },
        };
        Children.Add(_speaking);

        Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8),
            Padding = new Thickness(6, 4, 10, 4),
            CornerRadius = new CornerRadius(12),
            Background = Black(0x8C),
            IsHitTestVisible = false,
            Child = Row(6, _status, _label).With(r => r.VerticalAlignment = VerticalAlignment.Center),
        });

        DoubleTapped += (_, e) =>
        {
            Participant.ToggleFit();
            e.Handled = true;
        };
        SizeChanged += (_, _) => UpdateSize();

        _bindings.Observe(participant, Update,
            nameof(GroupParticipant.Video), nameof(GroupParticipant.ShowVideo), nameof(GroupParticipant.State),
            nameof(GroupParticipant.VideoHidden), nameof(GroupParticipant.PlaceholderTitle));
        _bindings.Observe(participant, () => _video.Fit = participant.Fit ? VideoFit.Fit : VideoFit.Fill, nameof(GroupParticipant.Fit));
        _bindings.Observe(participant, () => _speaking.Opacity = participant.Speaking ? 1 : 0, nameof(GroupParticipant.Speaking));
        _bindings.Observe(participant, () => SetLevel(participant.AudioLevel), nameof(GroupParticipant.AudioLevel));
        var (start, end) = Avatar.Colors(participant.AvatarSeed);
        _avatarFill.Fill = Gradient(start, end);
        _label.Text = participant.Label;
        ToolTipService.SetToolTip(this, participant.Label + " · double-click to fit or fill");
        AutomationProperties.SetName(this, participant.Label);
    }

    public GroupParticipant Participant { get; }

    private void Update()
    {
        var p = Participant;
        // No sink while the avatar shows: frames of a hidden tile are not converted or drawn.
        _video.Feed = p.ShowVideo ? p.Video : null;
        _video.Visibility = Visible(p.ShowVideo);
        _placeholder.Opacity = p.ShowVideo ? 0 : 1;
        _placeholder.IsHitTestVisible = !p.ShowVideo;
        _placeholderTitle.Text = p.PlaceholderTitle;
        _status.Status = StatusSlot.For(!p.MicOff, p.IsPresenting);
    }

    private void UpdateSize()
    {
        var roomy = ActualHeight >= RoomyHeight;
        var size = roomy ? 96 : Math.Clamp(ActualHeight * 0.36, 36, 72);
        _avatar.Width = _avatar.Height = size;
        _avatarFill.Width = _avatarFill.Height = size;
        _ring.Width = _ring.Height = size;
        _ring.CenterPoint = new Vector3((float)size / 2, (float)size / 2, 0);
        _avatarIcon.FontSize = size * 0.4;
        _placeholderTitle.Visibility = Visible(roomy);
    }

    /// <summary>Audio level 0..1, amplified like the 1:1 placeholder (speech is quiet in these units).</summary>
    private void SetLevel(double level)
    {
        var amount = (float)Math.Clamp(level * 3, 0, 1);
        _ring.Opacity = amount;
        var scale = 1 + 0.3f * amount;
        _ring.Scale = new Vector3(scale, scale, 1);
    }

    public void Dispose()
    {
        _bindings.Dispose();
        _video.Feed = null;
    }
}
