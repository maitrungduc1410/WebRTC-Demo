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
/// The self view (web: LocalTile.vue). Alone in the room it is the whole stage, with a large
/// microphone level on the left edge. With someone to show it is a draggable tile that springs to
/// the corner a throw points at, like the iOS and Android clients; position and size spring
/// together, so it shrinks into its corner when someone joins.
/// </summary>
internal sealed class LocalTile : Grid
{
    private static readonly Spring Snappy = new(380, 34);
    /// <summary>Between the whole stage and the tile.</summary>
    private static readonly Spring Soft = new(170, 24);
    /// <summary>To the corner a throw points at.</summary>
    private static readonly Spring Thrown = new(300, 28);
    /// <summary>Seconds of the release velocity added to the position to pick the corner.</summary>
    private const double ThrowLookahead = 0.18;
    private const double EdgeMargin = 16;
    private const double SmallAvatar = 48;
    private const double LargeAvatar = 112;

    private readonly VideoView _video = new() { Fit = VideoFit.Fill };
    private readonly Grid _cameraOff = new() { Background = Brush(0x26262C) };
    private readonly Grid _avatar;
    private readonly TextBlock _avatarText = Text("You", 13, FontWeights.SemiBold, White());
    private readonly TextBlock _cameraOffText = Text("Camera off", 12, foreground: White(0xB0));
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
    /// <summary>Web: the lg MicLevel, 24 px from the left edge and vertically centred.</summary>
    private readonly MicLevelIndicator _stageMicLevel = new(large: true)
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(24, 0, 0, 0),
    };
    private readonly Border _pill;
    private bool _micOn = true;
    private bool _wholeStage;
    private readonly TranslateTransform _translate = new();
    private readonly Stopwatch _clock = new();
    private FrameworkElement? _stage;
    private Vector2 _position;
    private Vector2 _velocity;
    private Vector2 _target;
    private Vector2 _size;
    private Vector2 _sizeVelocity;
    private Vector2 _sizeTarget;
    private Spring _spring = Snappy;
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
        RenderTransform = _translate;
        Shadow = new ThemeShadow();
        BorderBrush = White(0x30);

        _video.Mirrored = true;
        _video.FrameSizeChanged += (w, h) =>
        {
            _aspect = Math.Clamp((double)w / h, 0.5, 2.4);
            Relayout();
        };
        Children.Add(_video);

        var (start, end) = Avatar.Colors(Avatar.LocalSeed);
        _avatar = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new Ellipse { Fill = Gradient(start, end) },
                _avatarText.With(t =>
                {
                    t.HorizontalAlignment = HorizontalAlignment.Center;
                    t.VerticalAlignment = VerticalAlignment.Center;
                }),
            },
        };
        _cameraOff.Children.Add(Column(8, _avatar, _cameraOffText.With(t => t.HorizontalAlignment = HorizontalAlignment.Center))
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
        Children.Add(_stageMicLevel);
        SizeChanged += (_, _) => UpdatePillWidth();

        ManipulationStarted += (_, _) =>
        {
            _dragging = true;
            StopAnimation();
        };
        ManipulationDelta += (_, e) =>
        {
            if (!_dragging) return;
            _position += new Vector2((float)e.Delta.Translation.X, (float)e.Delta.Translation.Y);
            Apply();
        };
        ManipulationCompleted += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            // WinUI reports DIPs per millisecond.
            var velocity = new Vector2((float)e.Velocities.Linear.X, (float)e.Velocities.Linear.Y) * 1000;
            var projected = _position + velocity * (float)ThrowLookahead + _size / 2;
            _right = projected.X > _stage!.ActualWidth / 2;
            _bottom = projected.Y > _stage.ActualHeight / 2;
            _velocity = velocity;
            AnimateTo(CornerPosition(), _sizeTarget, Thrown);
        };
        Unloaded += (_, _) => StopAnimation();
        ApplyMode();
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
        _stageMicLevel.SetMuted(!on);
        UpdateMicLevels();
        UpdatePillWidth();
    }

    public void SetMicLevel(double level)
    {
        _micLevel.SetLevel(level);
        _stageMicLevel.SetLevel(level);
    }

    /// <summary>
    /// Alone in the room (nobody else to show): the self view fills the stage and can't be dragged;
    /// otherwise it is the corner tile.
    /// </summary>
    public void SetWholeStage(bool wholeStage)
    {
        if (wholeStage == _wholeStage) return;
        _wholeStage = wholeStage;
        // A drag in progress ends here: the rest of it would move the stage.
        _dragging = false;
        ApplyMode();
        Relayout(Soft);
    }

    private void ApplyMode()
    {
        var tile = !_wholeStage;
        CornerRadius = new CornerRadius(tile ? 12 : 0);
        BorderThickness = new Thickness(tile ? 1 : 0);
        // Lifted above the stage (with its shadow) only as a tile.
        Translation = new Vector3(0, 0, tile ? 32 : 0);
        ManipulationMode = tile ? ManipulationModes.TranslateX | ManipulationModes.TranslateY : ManipulationModes.None;
        // Web: pointer-events-none while it is the stage, so input goes to the stage behind.
        IsHitTestVisible = tile;
        _pill.Visibility = Visible(tile);
        _avatar.Width = _avatar.Height = tile ? SmallAvatar : LargeAvatar;
        _avatarText.FontSize = tile ? 13 : 28;
        _cameraOffText.Text = tile ? "Camera off" : "Your camera is off";
        _cameraOffText.FontSize = tile ? 12 : 16;
        UpdateMicLevels();
    }

    private void UpdateMicLevels()
    {
        _micLevel.SetShown(!_wholeStage && _micOn);
        _stageMicLevel.SetShown(_wholeStage);
    }

    /// <summary>Web: max-w-[calc(100%-3rem)] beside the level, 100% - 1rem without it.</summary>
    private void UpdatePillWidth() =>
        _pill.MaxWidth = Math.Max(0, ActualWidth - (_micOn ? 2 * 8 + MicLevelIndicator.SmallSize + 4 : 2 * 8));

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
        if (!_dragging && _placed && !_wholeStage) AnimateTo(CornerPosition(), _sizeTarget, Snappy);
    }

    /// <summary>The stage or the frame changed size: as the whole stage it follows the edges at once.</summary>
    private void Relayout() => Relayout(_wholeStage ? null : Snappy);

    /// <param name="spring">Null jumps.</param>
    private void Relayout(Spring? spring)
    {
        if (_stage == null || _stage.ActualWidth <= 0) return;
        var stageWidth = _stage.ActualWidth;
        Vector2 size;
        if (_wholeStage)
        {
            size = new Vector2((float)stageWidth, (float)_stage.ActualHeight);
        }
        else
        {
            var width = stageWidth < 640 ? stageWidth * 0.32 : Math.Clamp(stageWidth * 0.2, 220, 320);
            size = new Vector2((float)width, (float)Math.Min(width / _aspect, _stage.ActualHeight * 0.45));
        }
        _sizeTarget = size;
        if (_dragging) return;
        var position = _wholeStage ? Vector2.Zero : CornerPosition();
        if (!_placed)
        {
            _placed = true;
            _position = position;
            _size = size;
            Apply();
        }
        else
        {
            AnimateTo(position, size, spring);
        }
    }

    private Vector2 CornerPosition()
    {
        if (_stage == null) return default;
        var x = _right ? _stage.ActualWidth - _sizeTarget.X - EdgeMargin - _insets.Right : EdgeMargin + _insets.Left;
        var y = _bottom ? _stage.ActualHeight - _sizeTarget.Y - EdgeMargin - _insets.Bottom : EdgeMargin + _insets.Top;
        return new Vector2((float)Math.Max(0, x), (float)Math.Max(0, y));
    }

    private void AnimateTo(Vector2 position, Vector2 size, Spring? spring)
    {
        _target = position;
        _sizeTarget = size;
        if (spring is not { } chosen || !Motion.AnimationsEnabled)
        {
            StopAnimation();
            _position = position;
            _size = size;
            _velocity = _sizeVelocity = Vector2.Zero;
            Apply();
            return;
        }
        _spring = chosen;
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
        Step(ref _position, ref _velocity, _target, _spring, dt);
        Step(ref _size, ref _sizeVelocity, _sizeTarget, _spring, dt);
        if (Settled(_position, _velocity, _target) && Settled(_size, _sizeVelocity, _sizeTarget))
        {
            _position = _target;
            _size = _sizeTarget;
            _velocity = _sizeVelocity = Vector2.Zero;
            StopAnimation();
        }
        Apply();
    }

    private static void Step(ref Vector2 value, ref Vector2 velocity, Vector2 target, Spring spring, float dt)
    {
        var acceleration = -(float)spring.Stiffness * (value - target) - (float)spring.Damping * velocity;
        velocity += acceleration * dt;
        value += velocity * dt;
    }

    private static bool Settled(Vector2 value, Vector2 velocity, Vector2 target) =>
        Vector2.Distance(value, target) < 0.5f && velocity.Length() < 5;

    private void Apply()
    {
        _translate.X = _position.X;
        _translate.Y = _position.Y;
        Width = Math.Max(1, _size.X);
        Height = Math.Max(1, _size.Y);
    }

    /// <summary>Web: the motion-v springs of LocalTile.vue.</summary>
    private readonly record struct Spring(double Stiffness, double Damping);
}