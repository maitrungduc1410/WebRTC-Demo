using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Interop;

namespace WebRtcDemo.App.Video;

/// <summary>
/// Renders an <see cref="IVideoFeed"/>. Frames arrive on a WebRTC thread and go into a one-slot
/// mailbox; the UI thread draws the newest one on the next composition tick, so a slow UI drops
/// frames instead of queueing them.
/// <para>
/// As on iOS, Android and macOS, the video is always laid out just large enough to cover the view
/// (the clip crops the rest); fit only scales it down, on the panel's composition visual. So a
/// fit/fill change is a GPU-only spring: no layout, no swap chain change, no redraw at another size.
/// </para>
/// </summary>
internal sealed class VideoView : Grid
{
    /// <summary>The spring of the other clients (iOS response 0.5 s, damping fraction 0.86).</summary>
    private const float SpringDamping = 0.86f;
    private static readonly TimeSpan SpringPeriod = TimeSpan.FromSeconds(0.5);
    private static readonly Windows.UI.ViewManagement.UISettings SystemSettings = new();

    // A Canvas never clips or constrains the oversized panel; a Grid would give it a layout clip
    // in the panel's own coordinates, which the fit scale would shrink with the video.
    // Not hit-testable: the filled panel overflows the view, and input over a neighbouring tile
    // must reach that tile; the view's own background takes it within its bounds.
    private readonly Canvas _host = new() { IsHitTestVisible = false };
    private readonly SwapChainPanel _panel = new();
    private readonly Visual _visual;
    private SpringVector3NaturalMotionAnimation? _spring;
    private bool _laidOut;
    /// <summary>The frame or the view changed size since the last composition tick.</summary>
    private bool _sizeChangedThisTick;
    private float _fitScale = 1;
    private readonly RectangleGeometry _clip = new();
    private readonly Lock _gate = new();
    private SwapChainRenderer? _renderer;
    private IVideoFeed? _feed;
    private IDisposable? _subscription;
    private bool _rendering;

    // Mailbox, guarded by _gate.
    private byte[] _incoming = [];
    private int _incomingWidth;
    private int _incomingHeight;
    private bool _hasFrame;
    private byte[] _drawing = [];

    private int _frameWidth;
    private int _frameHeight;
    private VideoFit _fit = VideoFit.Fill;
    private bool _mirrored;

    public VideoView()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        Clip = _clip;
        _host.Children.Add(_panel);
        Children.Add(_host);
        _visual = ElementCompositionPreview.GetElementVisual(_panel);
        SizeChanged += (_, _) =>
        {
            _clip.Rect = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight);
            UpdateLayoutRect(animate: false);
            _sizeChangedThisTick = true;
        };
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) =>
        {
            // Settle where it was going, so a reload starts from the right scale.
            ApplyFitScale(_fitScale, animate: false);
            UpdateSubscription();
            _renderer?.Dispose();
            _renderer = null;
        };
    }

    /// <summary>
    /// Changing <see cref="Fit"/> springs between the two instead of jumping (remote videos): a
    /// double-click, or the call's automatic fit when a screen share starts or stops. A fit picked
    /// in reaction to a new frame size or a resized view (in the same composition tick, e.g. from
    /// <see cref="FrameSizeChanged"/>) jumps, as do the first frame and changes while Windows has
    /// animations turned off.
    /// </summary>
    public bool AnimatesFit { get; set; }

    /// <summary>The size of the last frame drawn changed (UI thread).</summary>
    public event Action<int, int>? FrameSizeChanged;

    public int FrameWidth => _frameWidth;
    public int FrameHeight => _frameHeight;

    public IVideoFeed? Feed
    {
        get => _feed;
        set
        {
            if (ReferenceEquals(_feed, value)) return;
            _feed = value;
            _subscription?.Dispose();
            _subscription = null;
            _laidOut = false;
            lock (_gate) _hasFrame = false;
            UpdateSubscription();
        }
    }

    public VideoFit Fit
    {
        get => _fit;
        set
        {
            if (_fit == value) return;
            _fit = value;
            UpdateLayoutRect(animate: AnimatesFit && _laidOut && IsLoaded && !_sizeChangedThisTick);
        }
    }

    /// <summary>Mirrors horizontally, as a selfie view (part of the visual's scale, around its centre).</summary>
    public bool Mirrored
    {
        get => _mirrored;
        set
        {
            if (_mirrored == value) return;
            _mirrored = value;
            ApplyFitScale(_fitScale, animate: false);
        }
    }

    private void UpdateSubscription()
    {
        var active = IsLoaded && _feed != null;
        if (active && _subscription == null) _subscription = _feed!.Subscribe(OnFrame);
        if (!active)
        {
            _subscription?.Dispose();
            _subscription = null;
        }
        if (active != _rendering)
        {
            _rendering = active;
            if (active) CompositionTarget.Rendering += OnRendering;
            else CompositionTarget.Rendering -= OnRendering;
        }
        _panel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>On a WebRTC thread: keep a tightly packed copy of the newest frame.</summary>
    private void OnFrame(VideoFrame frame)
    {
        var rowBytes = frame.Width * 4;
        var size = rowBytes * frame.Height;
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < rowBytes || frame.Data.Length < frame.Stride * (frame.Height - 1) + rowBytes) return;
        lock (_gate)
        {
            if (_incoming.Length != size) _incoming = new byte[size];
            if (frame.Stride == rowBytes)
            {
                frame.Data[..size].CopyTo(_incoming);
            }
            else
            {
                for (var y = 0; y < frame.Height; y++) frame.Data.Slice(y * frame.Stride, rowBytes).CopyTo(_incoming.AsSpan(y * rowBytes));
            }
            _incomingWidth = frame.Width;
            _incomingHeight = frame.Height;
            _hasFrame = true;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        _sizeChangedThisTick = false;
        int width, height;
        lock (_gate)
        {
            if (!_hasFrame) return;
            _hasFrame = false;
            (_incoming, _drawing) = (_drawing, _incoming);
            width = _incomingWidth;
            height = _incomingHeight;
        }
        var created = _renderer == null;
        _renderer ??= new SwapChainRenderer(_panel);
        _renderer.Draw(_drawing, width, height);
        var resized = width != _frameWidth || height != _frameHeight;
        _frameWidth = width;
        _frameHeight = height;
        // !_laidOut: the first frame of a new feed, which may have the old size (camera off and on).
        if (created || resized || !_laidOut) UpdateLayoutRect(animate: false);
        if (resized)
        {
            _sizeChangedThisTick = true;
            FrameSizeChanged?.Invoke(width, height);
        }
    }

    /// <summary>
    /// Lays the panel out at the cover size, centred (only when the frame size or the view
    /// changes), and scales its visual to the fit.
    /// </summary>
    private void UpdateLayoutRect(bool animate)
    {
        if (VideoLayout.CoverScale(_frameWidth, _frameHeight, ActualWidth, ActualHeight) is not { } cover) return;
        var width = _frameWidth * cover;
        var height = _frameHeight * cover;
        if (_panel.Width != width || _panel.Height != height)
        {
            _panel.Width = width;
            _panel.Height = height;
            _visual.CenterPoint = new Vector3((float)(width / 2), (float)(height / 2), 0);
        }
        Canvas.SetLeft(_panel, (ActualWidth - width) / 2);
        Canvas.SetTop(_panel, (ActualHeight - height) / 2);
        // The swap chain stays at the frame's size; this is only its matrix, DIPs per frame pixel.
        if (_renderer != null) _renderer.Scale = (float)cover;
        _laidOut = true;
        ApplyFitScale((float)VideoLayout.FitScale(_fit, _frameWidth, _frameHeight, ActualWidth, ActualHeight), animate);
    }

    private void ApplyFitScale(float scale, bool animate)
    {
        _fitScale = scale;
        var value = new Vector3(_mirrored ? -scale : scale, scale, 1);
        if (animate && SystemSettings.AnimationsEnabled)
        {
            if (_spring == null)
            {
                _spring = _visual.Compositor.CreateSpringVector3Animation();
                _spring.DampingRatio = SpringDamping;
                _spring.Period = SpringPeriod;
            }
            _spring.FinalValue = value;
            _visual.StartAnimation("Scale", _spring);
        }
        else
        {
            _visual.StopAnimation("Scale");
            _visual.Scale = value;
        }
    }
}
