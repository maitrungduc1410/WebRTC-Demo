using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WebRtcDemo.Core.Call;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>
/// The local microphone's level on the self view (web: MicLevel.vue): three white bars in a dark
/// circle. Small in the picture-in-picture tile, where the circle pops in with the web's bouncy
/// curve when the mic comes on and shrinks away when it is muted; large on the left edge while the
/// self view is the whole stage, where muting turns it red with a crossed-out mic instead. Bar
/// heights ease out over 100 ms. With Windows animations off everything jumps.
/// </summary>
internal sealed class MicLevelIndicator : Grid
{
    public const double SmallSize = 28;
    private const double LargeSize = 48;
    private static readonly double[] Gains = [0.6, 1, 0.6];
    private static readonly TimeSpan BarDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MuteDuration = TimeSpan.FromMilliseconds(300);

    private readonly double _minBar;
    private readonly double _maxBar;
    private readonly Ellipse _circle = new() { Fill = Black(0x8C) };
    /// <summary>The bars, centred in a layer the circle's size, so muting scales them about its centre.</summary>
    private readonly Grid _barRow = new();
    private readonly FontIcon? _mutedIcon;
    private bool _muted;
    private double _level;
    private readonly Rectangle[] _bars;
    private readonly double[] _barFrom = new double[3];
    private readonly double[] _barTo = new double[3];
    private readonly Stopwatch _barClock = new();
    private readonly Stopwatch _showClock = new();
    private bool _shown = true;
    private double _shownFrom = 1;
    private double _shownValue = 1;
    private bool _barsMoving;
    private bool _showMoving;
    private bool _ticking;

    public MicLevelIndicator(bool large = false)
    {
        // Web: size-12 with 5 px bars of 5..22 px (lg), size-7 with 3 px bars of 3..12 px (sm).
        var size = large ? LargeSize : SmallSize;
        var barWidth = large ? 5.0 : 3.0;
        _minBar = barWidth;
        _maxBar = large ? 22 : 12;
        Width = Height = size;
        IsHitTestVisible = false;
        CenterPoint = new Vector3((float)size / 2, (float)size / 2, 0);
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Children.Add(_circle);
        _bars = new Rectangle[Gains.Length];
        var bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = large ? 4 : 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _barRow.Children.Add(bars);
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = barWidth,
                Height = _minBar,
                RadiusX = barWidth / 2,
                RadiusY = barWidth / 2,
                Fill = White(),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _barFrom[i] = _barTo[i] = _minBar;
            bars.Children.Add(_bars[i]);
        }
        Children.Add(_barRow);
        if (large)
        {
            _mutedIcon = Icon(Glyphs.MicOff, 20).With(i =>
            {
                i.Foreground = White();
                i.HorizontalAlignment = HorizontalAlignment.Center;
                i.VerticalAlignment = VerticalAlignment.Center;
                i.Opacity = 0;
            });
            Children.Add(_mutedIcon);
            foreach (var part in new UIElement[] { _barRow, _mutedIcon })
            {
                part.OpacityTransition = new ScalarTransition { Duration = MuteDuration };
                part.ScaleTransition = new Vector3Transition { Duration = MuteDuration };
                part.CenterPoint = new Vector3((float)size / 2, (float)size / 2, 0);
            }
            _barRow.Width = _barRow.Height = size;
            _mutedIcon.Width = _mutedIcon.Height = size;
            _mutedIcon.Scale = new Vector3(0.5f, 0.5f, 1);
        }
        // Removed mid-animation: show the end state, not a frozen frame.
        Unloaded += (_, _) =>
        {
            if (_ticking) Snap();
        };
    }

    private static bool Animated(FrameworkElement element) => element.IsLoaded && Motion.AnimationsEnabled;

    /// <summary>The level in [0, 1]; each bar is that times its gain, between its rest and full height.</summary>
    public void SetLevel(double level)
    {
        _level = level;
        if (_muted) level = 0;
        var changed = false;
        for (var i = 0; i < _bars.Length; i++)
        {
            var to = _minBar + (_maxBar - _minBar) * Math.Clamp(level * Gains[i], 0, 1);
            changed |= to != _barTo[i];
            _barTo[i] = to;
        }
        if (!changed) return;
        if (!Animated(this) || !_shown)
        {
            _barsMoving = false;
            for (var i = 0; i < _bars.Length; i++) _bars[i].Height = _barTo[i];
            StopIfIdle();
            return;
        }
        for (var i = 0; i < _bars.Length; i++) _barFrom[i] = _bars[i].Height;
        _barClock.Restart();
        _barsMoving = true;
        StartTicking();
    }

    /// <summary>Large only: red with a crossed-out mic while muted, the bars otherwise.</summary>
    public void SetMuted(bool muted)
    {
        if (_mutedIcon == null || muted == _muted) return;
        _muted = muted;
        _circle.Fill = muted ? Brush(0xEF4444) : Black(0x8C);
        _mutedIcon.Opacity = muted ? 1 : 0;
        _mutedIcon.Scale = muted ? Vector3.One : new Vector3(0.5f, 0.5f, 1);
        _barRow.Opacity = muted ? 0 : 1;
        _barRow.Scale = muted ? new Vector3(0.5f, 0.5f, 1) : Vector3.One;
        SetLevel(_level);
    }

    /// <summary>Shrinks it away when hidden (muted in the tile, or the tile mode it isn't for).</summary>
    public void SetShown(bool shown)
    {
        if (shown == _shown) return;
        _shown = shown;
        if (!Animated(this))
        {
            _showMoving = false;
            _shownValue = shown ? 1 : 0;
            ApplyShown();
            StopIfIdle();
            return;
        }
        _shownFrom = _shownValue;
        _showClock.Restart();
        _showMoving = true;
        Visibility = Visibility.Visible;
        StartTicking();
    }

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        CompositionTarget.Rendering += OnTick;
    }

    private void StopIfIdle()
    {
        if (!_ticking || _barsMoving || _showMoving) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnTick;
    }

    private void Snap()
    {
        _barsMoving = _showMoving = false;
        for (var i = 0; i < _bars.Length; i++) _bars[i].Height = _barTo[i];
        _shownValue = _shown ? 1 : 0;
        ApplyShown();
        StopIfIdle();
    }

    private void OnTick(object? sender, object e)
    {
        if (_barsMoving)
        {
            var t = Math.Min(_barClock.Elapsed / BarDuration, 1);
            var eased = 1 - (1 - t) * (1 - t);
            for (var i = 0; i < _bars.Length; i++) _bars[i].Height = _barFrom[i] + (_barTo[i] - _barFrom[i]) * eased;
            _barsMoving = t < 1;
        }
        if (_showMoving)
        {
            var t = Math.Min(_showClock.Elapsed / (_shown ? EnterDuration : LeaveDuration), 1);
            var eased = _shown ? Motion.Bounce(t) : t * t;
            _shownValue = _shownFrom + ((_shown ? 1 : 0) - _shownFrom) * eased;
            _showMoving = t < 1;
            if (!_showMoving) _shownValue = _shown ? 1 : 0;
            ApplyShown();
        }
        StopIfIdle();
    }

    /// <summary>Scale 0.5 → 1 (with the curve's overshoot) and fade, as the web's scale-50 / opacity-0 transition.</summary>
    private void ApplyShown()
    {
        Opacity = Math.Clamp(_shownValue, 0, 1);
        var scale = (float)Math.Max(0.5 + 0.5 * _shownValue, 0);
        Scale = new Vector3(scale, scale, 1);
        Visibility = Visible(_shown || _shownValue > 0);
    }
}
