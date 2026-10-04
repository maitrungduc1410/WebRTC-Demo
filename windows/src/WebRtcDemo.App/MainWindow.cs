using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WebRtcDemo.App.Views;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Lobby;
using WebRtcDemo.Core.Media;

namespace WebRtcDemo.App;

/// <summary>One window: the lobby, or a call. Mica behind both; the call screen is always dark.</summary>
public sealed partial class MainWindow : Window
{
    private readonly Grid _root = new();
    private readonly TitleBar _titleBar;
    private readonly Grid _content = new();
    private readonly ToastPresenter _toasts = new();
    private readonly LobbyViewModel _lobbyViewModel;
    private LobbyView? _lobby;
    private CallViewModel? _call;
    private NativeCallMedia? _media;
    private CallView? _callView;

    public MainWindow()
    {
        var app = App.Current;
        Title = "WebRTC Demo";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // Taskbar, Alt+Tab and the picture-in-picture window (the same AppWindow).
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Resize(Scaled(1120, 760));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Scaled(480, 0).Width;
            presenter.PreferredMinimumHeight = Scaled(0, 400).Height;
        }

        _titleBar = new TitleBar
        {
            Title = "WebRTC Demo",
            IconSource = new ImageIconSource
            {
                ImageSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"))) { DecodePixelWidth = 32 },
            },
        };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(_titleBar);
        Grid.SetRow(_content, 1);
        _root.Children.Add(_content);
        Grid.SetRowSpan(_toasts, 2);
        _root.Children.Add(_toasts);
        Content = _root;
        SetTitleBar(_titleBar);

        _lobbyViewModel = new LobbyViewModel(app.Settings, app.ServerProbe, app.Dispatcher);
        _lobbyViewModel.JoinRequested += StartCall;
        ShowLobby();

        if (app.StartupError != null)
        {
            _root.Loaded += (_, _) => _toasts.Show(new Toast(app.StartupError, ToastKind.Error, Glyphs.Warning));
        }
    }

    public bool IsPictureInPicture => AppWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;

    public event Action? PictureInPictureChanged;

    public void TogglePictureInPicture()
    {
        if (IsPictureInPicture)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        }
        else
        {
            var overlay = CompactOverlayPresenter.Create();
            overlay.InitialSize = CompactOverlaySize.Medium;
            AppWindow.SetPresenter(overlay);
        }
        _titleBar.Title = IsPictureInPicture ? string.Empty : "WebRTC Demo";
        PictureInPictureChanged?.Invoke();
    }

    public void ShowToast(Toast toast) => _toasts.Show(toast);

    private void ShowLobby()
    {
        _lobby ??= new LobbyView(_lobbyViewModel);
        SetTheme(ElementTheme.Default);
        Swap(_lobby);
        _lobby.FocusRoom();
        _lobbyViewModel.Start();
    }

    private void StartCall(CallRequest request)
    {
        var app = App.Current;
        if (_call != null) return;
        if (app.Factory == null)
        {
            _toasts.Show(new Toast(app.StartupError ?? "WebRTC is not available", ToastKind.Error, Glyphs.Warning));
            return;
        }
        _media = new NativeCallMedia(app.Factory, app.Dispatcher, EffectsAcceleration.Setup());
        _call = new CallViewModel(app.Signaling, _media, app.Dispatcher, app.Settings);
        _call.ToastRequested += _toasts.Show;
        _call.Ended += OnCallEnded;
        // Joined first: the call screen is built for the room and mode.
        if (request.Group) _call.JoinGroup(request.RoomId, request.E2ee, request.ServerUrl);
        else _call.Join(request.RoomId, request.E2ee, request.ServerUrl);
        _callView = new CallView(_call, this);
        _lobbyViewModel.Stop();
        SetTheme(ElementTheme.Dark);
        Swap(_callView);
    }

    private void OnCallEnded()
    {
        // After the current handler, so a toast raised right after Ended ("room is full") still shows.
        DispatcherQueue.TryEnqueue(() =>
        {
            TearDownCall(restoreWindow: true);
            ShowLobby();
        });
    }

    /// <summary>Leaves the call, if any (the window is closing).</summary>
    /// <returns>The call's sockets closing.</returns>
    public Task EndCall()
    {
        var call = _call;
        TearDownCall(restoreWindow: false);
        _lobbyViewModel.Dispose();
        return call?.SocketsClosed ?? Task.CompletedTask;
    }

    private void TearDownCall(bool restoreWindow)
    {
        if (_call == null) return;
        if (restoreWindow && IsPictureInPicture) TogglePictureInPicture();
        _call.Ended -= OnCallEnded;
        _callView?.Dispose();
        _call.Dispose();
        _call.ToastRequested -= _toasts.Show;
        _media?.Dispose();
        _callView = null;
        _call = null;
        _media = null;
    }

    private void Swap(UIElement view)
    {
        _content.Children.Clear();
        _content.Children.Add(view);
    }

    private void SetTheme(ElementTheme theme)
    {
        _root.RequestedTheme = theme;
        var dark = theme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = dark ? Colors.White : null;
        titleBar.ButtonHoverForegroundColor = dark ? Colors.White : null;
        titleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : null;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private Windows.Graphics.SizeInt32 Scaled(int width, int height)
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        return new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale));
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
