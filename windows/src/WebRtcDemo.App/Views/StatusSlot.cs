using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WebRtcDemo.Core.Call;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>What a name pill says about its person; <see cref="None"/> collapses the slot.</summary>
internal enum PillStatus { None, Live, Presenting, Muted }

/// <summary>
/// The status mark at the start of a name pill (web: GroupTile.vue / LocalTile.vue): a red
/// mic-off circle, a presenting glyph or a small emerald dot, in one slot whose width follows the
/// mark so the pill resizes smoothly. Marks pop in and out with the web's bouncy curve.
/// </summary>
internal sealed class StatusSlot : Canvas
{
    /// <summary>The muted red of every client (iOS #E5484D, web red-500).</summary>
    public const uint MutedRed = 0xE5484D;

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);
    private const double SlotHeight = 20;
    /// <summary>Collapsed, the slot pulls the next item back so it sits where the pill's padding would put it.</summary>
    private const double CollapsedMargin = -2;

    private readonly FrameworkElement[] _marks;
    private readonly double[] _shown = new double[4];
    private readonly double[] _shownFrom = new double[4];
    private readonly Stopwatch _clock = new();
    private double _width;
    private double _widthFrom;
    private double _margin;
    private double _marginFrom;
    private PillStatus _status;
    private bool _initialized;
    private bool _animating;

    /// <param name="glyphBrush">The presenting glyph's color; white for the dark pills over video.</param>
    public StatusSlot(Brush? glyphBrush = null)
    {
        Height = SlotHeight;
        VerticalAlignment = VerticalAlignment.Center;
        var muted = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = Brush(MutedRed),
            Child = Icon(Glyphs.MicOff, 10).With(i => i.Foreground = White()),
        };
        var presenting = Icon(Glyphs.Presenting, 13).With(i =>
        {
            i.Foreground = glyphBrush ?? White();
            i.Width = i.Height = 14;
        });
        var live = new Ellipse { Width = 6, Height = 6, Fill = Brush(0x34D399) };
        _marks = [new Border(), live, presenting, muted];
        Place(live, 2);
        Place(presenting, 1);
        Place(muted, 0);
        foreach (var mark in _marks[1..])
        {
            mark.Opacity = 0;
            Children.Add(mark);
        }
        // Removed mid-pop (a row re-added, a tile hidden): show the end state, not a frozen frame.
        Unloaded += (_, _) =>
        {
            if (_animating) Snap();
        };
    }

    private static void Place(FrameworkElement mark, double left)
    {
        SetLeft(mark, left);
        SetTop(mark, (SlotHeight - mark.Height) / 2);
        mark.CenterPoint = new Vector3((float)mark.Width / 2, (float)mark.Height / 2, 0);
    }

    private static double WidthOf(PillStatus status) => status switch
    {
        PillStatus.Muted => 20,
        PillStatus.Presenting => 16,
        PillStatus.Live => 8,
        _ => 0,
    };

    public PillStatus Status
    {
        get => _status;
        set
        {
            if (value == _status && _initialized) return;
            _status = value;
            AutomationProperties.SetName(this, value switch
            {
                PillStatus.Muted => "Microphone muted",
                PillStatus.Presenting => "Presenting",
                _ => string.Empty,
            });
            if (!_initialized || !IsLoaded)
            {
                // The first state shows at once, without a pop.
                _initialized = true;
                Snap();
                return;
            }
            Array.Copy(_shown, _shownFrom, _shown.Length);
            _widthFrom = _width;
            _marginFrom = _margin;
            _clock.Restart();
            if (_animating) return;
            _animating = true;
            CompositionTarget.Rendering += OnTick;
        }
    }

    /// <summary>Jumps to the current status's end state.</summary>
    private void Snap()
    {
        Stop();
        for (var i = 0; i < _shown.Length; i++) _shown[i] = i == (int)_status ? 1 : 0;
        _width = WidthOf(_status);
        _margin = _status == PillStatus.None ? CollapsedMargin : 0;
        Apply();
    }

    private void Stop()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnTick;
    }

    private void OnTick(object? sender, object e)
    {
        var t = Math.Min(_clock.Elapsed / Duration, 1);
        var eased = Motion.Bounce(t);
        for (var i = 0; i < _shown.Length; i++)
        {
            var to = i == (int)_status ? 1 : 0;
            _shown[i] = _shownFrom[i] + (to - _shownFrom[i]) * eased;
        }
        _width = _widthFrom + (WidthOf(_status) - _widthFrom) * eased;
        _margin = _marginFrom + ((_status == PillStatus.None ? CollapsedMargin : 0) - _marginFrom) * eased;
        Apply();
        if (t >= 1) Stop();
    }

    private void Apply()
    {
        Width = Math.Max(_width, 0);
        Margin = new Thickness(0, 0, _margin, 0);
        for (var i = 1; i < _marks.Length; i++)
        {
            var shown = _shown[i];
            _marks[i].Opacity = Math.Clamp(shown, 0, 1);
            // Scale 0.5 → 1 (with the curve's overshoot), as the web's scale-50 → scale-100.
            var scale = (float)Math.Max(0.5 + 0.5 * shown, 0);
            _marks[i].Scale = new Vector3(scale, scale, 1);
        }
    }

    /// <summary>Muted wins over presenting, as on the web.</summary>
    public static PillStatus For(bool micOn, bool presenting) =>
        !micOn ? PillStatus.Muted : presenting ? PillStatus.Presenting : PillStatus.Live;
}

internal static class Motion
{
    private static readonly Windows.UI.ViewManagement.UISettings SystemSettings = new();

    /// <summary>Windows Settings, Accessibility, Visual effects, Animation effects.</summary>
    public static bool AnimationsEnabled => SystemSettings.AnimationsEnabled;

    /// <summary>The web's --ease-bounce (web/src/style.css): a spring that overshoots by 8 % and settles.</summary>
    private static readonly double[] BouncePoints =
    [
        0, 0.0175, 0.0643, 0.1326, 0.2154, 0.3069, 0.402, 0.4969, 0.5883, 0.6739, 0.7521, 0.8219, 0.8827,
        0.9344, 0.9771, 1.0115, 1.0381, 1.0577, 1.0712, 1.0793, 1.0831, 1.0832, 1.0805, 1.0757, 1.0693,
        1.062, 1.0542, 1.0462, 1.0384, 1.031, 1.0241, 1.0179, 1.0124, 1.0077, 1.0037, 1.0005, 0.9979,
        0.996, 0.9946, 0.9937, 1,
    ];

    /// <summary>CSS linear(): the points evenly spaced over 0..1, interpolated linearly.</summary>
    public static double Bounce(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var position = t * (BouncePoints.Length - 1);
        var index = (int)position;
        return BouncePoints[index] + (BouncePoints[index + 1] - BouncePoints[index]) * (position - index);
    }
}
