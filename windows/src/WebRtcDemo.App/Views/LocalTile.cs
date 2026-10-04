using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WebRtcDemo.App.Video;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Media;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>
/// The self view: draggable, and on release it springs to the corner the throw points at, like
/// the iOS and Android clients.
/// </summary>
internal sealed class LocalTile : Grid
{
    private const double Stiffness = 380;
    private const double Damping = 34;
    /// <summary>Seconds of the release velocity added to the position to pick the corner.</summary>
    private const double ThrowLookahead = 0.18;
    private const double EdgeMargin = 16;

    private readonly VideoView _video = new() { Fit = VideoFit.Fill };
    private readonly Grid _cameraOff = new() { Background = Brush(0x26262C) };
    private readonly StatusSlot _labelMic = new();
    private readonly TextBlock _labelText = Text(string.Empty, 12, foreground: White(0xA6)).With(t =>
    {
        t.TextWrapping = TextWrapping.NoWrap;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
    });
    private readonly MicLevelIndicator _micLevel = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(8),
    };
    private readonly Border _pill;
    private bool _micOn = true;
    private readonly TranslateTransform _translate = new();
    private readonly Stopwatch _clock = new();
    private FrameworkElement? _stage;
    private Vector2 _position;
    private Vector2 _velocity;
    private Vector2 _target;
    private bool _dragging;
    private bool _animating;
    private bool _placed;
    private bool _right = true;
    private bool _bottom = true;
    private double _aspect = 16.0 / 9;
    private Thickness _insets;

    public LocalTile()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        CornerRadius = new CornerRadius(12);
        BorderBrush = White(0x30);
        BorderThickness = new Thickness(1);
        RenderTransform = _translate;
        Translation = new Vector3(0, 0, 32);
        Shadow = new ThemeShadow();
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;

        _video.Mirrored = true;
        _video.FrameSizeChanged += (w, h) =>
        {
            _aspect = Math.Clamp((double)w / h, 0.5, 2.4);
            Relayout();
        };
        Children.Add(_video);

        var (start, end) = Avatar.Colors(Avatar.LocalSeed);
        _cameraOff.Children.Add(Column(8,
            new Grid
            {
                Width = 48,
                Height = 48,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Ellipse { Fill = Gradient(start, end) },
                    Text("You", 13, FontWeights.SemiBold, White()).With(t =>
                    {
                        t.HorizontalAlignment = HorizontalAlignment.Center;
                        t.VerticalAlignment = VerticalAlignment.Center;
                    }),
                },
            },
            Text("Camera off", 12, foreground: White(0xB0)).With(t => t.HorizontalAlignment = HorizontalAlignment.Center))
            .With(c => c.VerticalAlignment = VerticalAlignment.Center));
        Children.Add(_cameraOff);

        // "You", and in a group call how the others see us; the red mic-off badge collapses
        // to nothing while the mic is on (web: LocalTile.vue).
        _labelMic.Status = PillStatus.None;
        var you = Text("You", 12, FontWeights.SemiBold, White());
        var label = new Grid
        {
            ColumnSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        label.Children.Add(_labelMic);
        label.Children.Add(you);
        label.Children.Add(_labelText);
        SetColumn(you, 1);
        SetColumn(_labelText, 2);
        Children.Add(_pill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8),
            Padding = new Thickness(6, 4, 10, 4),
            CornerRadius = new CornerRadius(12),
            Background = Black(0x8C),
            IsHitTestVisible = false,
            Child = label,
        });
        // While the mic is on, its level sits in the opposite corner; the pill already marks muted.
        Children.Add(_micLevel);
        SizeChanged += (_, _) => UpdatePillWidth();

        ManipulationStarted += (_, _) =>
        {
            _dragging = true;
            StopAnimation();
        };
        ManipulationDelta += (_, e) =>
        {
            _position += new Vector2((float)e.Delta.Translation.X, (float)e.Delta.Translation.Y);
            Apply();
        };
        ManipulationCompleted += (_, e) =>
        {
            _dragging = false;
            // WinUI reports DIPs per millisecond.
            var velocity = new Vector2((float)e.Velocities.Linear.X, (float)e.Velocities.Linear.Y) * 1000;
            var projected = _position + velocity * (float)ThrowLookahead + new Vector2((float)Width, (float)Height) / 2;
            _right = projected.X > _stage!.ActualWidth / 2;
            _bottom = projected.Y > _stage.ActualHeight / 2;
            _velocity = velocity;
            AnimateTo(CornerPosition());
        };
        Unloaded += (_, _) => StopAnimation();
    }

    public void Attach(FrameworkElement stage)
    {
        _stage = stage;
        stage.SizeChanged += (_, _) => Relayout();
    }

    public void SetFeed(IVideoFeed? feed, bool mirrored, bool showVideo)
    {
        _video.Feed = showVideo ? feed : null;
        _video.Mirrored = mirrored;
        _video.Visibility = Visible(showVideo && feed != null);
        _cameraOff.Visibility = Visible(!(showVideo && feed != null));
    }

    public void SetMicOn(bool on)
    {
        if (on == _micOn) return;
        _micOn = on;
        _labelMic.Status = on ? PillStatus.None : PillStatus.Muted;
        _micLevel.SetShown(on);
        UpdatePillWidth();
    }

    public void SetMicLevel(double level) => _micLevel.SetLevel(level);

    /// <summary>Web: max-w-[calc(100%-3rem)] beside the level, 100% - 1rem without it.</summary>
    private void UpdatePillWidth() =>
        _pill.MaxWidth = Math.Max(0, ActualWidth - (_micOn ? 2 * 8 + MicLevelIndicator.Size + 4 : 2 * 8));

    /// <summary>Group call: how the others see this device, after "You"; null shows "You" only.</summary>
    public void SetLabel(string? label)
    {
        _labelText.Text = label != null ? "· " + label : string.Empty;
        _labelText.Visibility = Visible(label != null);
    }

    /// <summary>Keeps the tile clear of the top bar and toolbar while they show.</summary>
    public void SetInsets(Thickness insets)
    {
        _insets = insets;
        if (!_dragging && _placed) AnimateTo(CornerPosition());
    }

    private void Relayout()
    {
        if (_stage == null || _stage.ActualWidth <= 0) return;
        var stageWidth = _stage.ActualWidth;
        var width = stageWidth < 640 ? stageWidth * 0.32 : Math.Clamp(stageWidth * 0.2, 220, 320);
        Width = width;
        Height = Math.Min(width / _aspect, _stage.ActualHeight * 0.45);
        if (_dragging) return;
        if (!_placed)
        {
            _placed = true;
            _position = CornerPosition();
            Apply();
        }
        else
        {
            AnimateTo(CornerPosition());
        }
    }

    private Vector2 CornerPosition()
    {
        if (_stage == null) return default;
        var x = _right ? _stage.ActualWidth - Width - EdgeMargin - _insets.Right : EdgeMargin + _insets.Left;
        var y = _bottom ? _stage.ActualHeight - Height - EdgeMargin - _insets.Bottom : EdgeMargin + _insets.Top;
        return new Vector2((float)Math.Max(0, x), (float)Math.Max(0, y));
    }

    private void AnimateTo(Vector2 target)
    {
        _target = target;
        if (_animating) return;
        _animating = true;
        _clock.Restart();
        CompositionTarget.Rendering += OnTick;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnTick;
    }

    /// <summary>A damped spring, integrated per frame (semi-implicit Euler, mass 1).</summary>
    private void OnTick(object? sender, object e)
    {
        var dt = (float)Math.Min(_clock.Elapsed.TotalSeconds, 1.0 / 30);
        _clock.Restart();
        var acceleration = -(float)Stiffness * (_position - _target) - (float)Damping * _velocity;
        _velocity += acceleration * dt;
        _position += _velocity * dt;
        if (Vector2.Distance(_position, _target) < 0.5f && _velocity.Length() < 5)
        {
            _position = _target;
            _velocity = Vector2.Zero;
            StopAnimation();
        }
        Apply();
    }

    private void Apply()
    {
        _translate.X = _position.X;
        _translate.Y = _position.Y;
    }
}
