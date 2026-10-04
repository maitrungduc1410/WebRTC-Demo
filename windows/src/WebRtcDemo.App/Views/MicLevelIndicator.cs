using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>
/// The local microphone's level on the self view (web: MicLevel.vue, size sm): three white bars
/// in a dark circle. Bar heights ease out over 100 ms; the circle pops in with the web's bouncy
/// curve when the mic comes on and shrinks away when it is muted. With Windows animations off
/// both jump.
/// </summary>
internal sealed class MicLevelIndicator : Grid
{
    public const double Size = 28;
    private const double BarWidth = 3;
    private const double MinBar = 3;
    private const double MaxBar = 12;
    private static readonly double[] Gains = [0.6, 1, 0.6];
    private static readonly TimeSpan BarDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(200);

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

    public MicLevelIndicator()
    {
        Width = Height = Size;
        IsHitTestVisible = false;
        CenterPoint = new Vector3((float)Size / 2, (float)Size / 2, 0);
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Children.Add(new Ellipse { Fill = Black(0x8C) });
        _bars = new Rectangle[Gains.Length];
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = BarWidth,
                Height = MinBar,
                RadiusX = BarWidth / 2,
                RadiusY = BarWidth / 2,
                Fill = White(),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _barFrom[i] = _barTo[i] = MinBar;
            row.Children.Add(_bars[i]);
        }
        Children.Add(row);
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
        var changed = false;
        for (var i = 0; i < _bars.Length; i++)
        {
            var to = MinBar + (MaxBar - MinBar) * Math.Clamp(level * Gains[i], 0, 1);
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

    /// <summary>Shown while the mic is on; hiding it (muted) shrinks it away.</summary>
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
