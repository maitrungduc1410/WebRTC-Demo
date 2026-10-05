using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WebRtcDemo.App.Ui;
using WebRtcDemo.App.Video;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Interop;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>
/// The call screen: remote video (or its placeholder) filling the stage, the draggable self view,
/// a top bar and a toolbar that hide after 4 s without input, and the chat panel on the right.
/// </summary>
internal sealed class CallView : Grid, IDisposable
{
    private static readonly TimeSpan ControlsTimeout = TimeSpan.FromSeconds(4);
    private const double TopInset = 64;
    private const double BottomInset = 92;

    private readonly CallViewModel _vm;
    private readonly MainWindow _window;
    private readonly Bindings _bindings = new();
    private readonly Grid _stage = new() { Background = Brush(0x0B0B0F) };
    private readonly VideoView _remote = new() { AnimatesFit = true };
    private readonly RemotePlaceholder _placeholder = new();
    private readonly LocalTile _local = new();
    private readonly Border _waiting;
    private readonly ChatPanel _chat;
    private readonly EffectsPanel _effectsPanel;
    private readonly Grid _topBar = new() { VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(16, 12, 16, 0) };
    private readonly Border _toolbar;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clockTimer;
    private bool _controlsVisible = true;
    private bool _pointerOverControls;
    private int _openFlyouts;
    private bool _effectsPanelShown;

    // Toolbar
    private readonly Button _mic;
    private readonly Button _camera;
    private readonly Button _effects;
    private readonly Button _share;
    private readonly Button _chatButton;
    private readonly InfoBadge _unread = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly Button _more;
    private readonly Button _leave;
    private readonly Button _pip;
    private readonly Button _exitPip;
    private readonly Grid _chatHost;

    // Top bar
    private readonly TextBlock _timer = Text(string.Empty, 13, FontWeights.SemiBold, White());
    private readonly Border _timerChip;
    private readonly Border _e2eeChip;
    private readonly Button _people;
    private readonly PeopleFlyout _peopleFlyout;
    private readonly TextBlock _peopleText = Text(string.Empty, 13, FontWeights.SemiBold, White());
    private readonly Border _sharingChip;
    private readonly GroupStage _groupStage;
    private readonly TextBlock _sharingText = Text(string.Empty, 13, foreground: White());

    // Waiting card
    private readonly TextBlock _waitingTitle = Text(string.Empty, 22, FontWeights.SemiBold, White());
    private readonly TextBlock _waitingSubtitle = Text(string.Empty, 14, foreground: White(0xB0));
    private readonly ProgressRing _waitingRing = new() { Width = 28, Height = 28, Foreground = White() };
    private readonly StackPanel _roomRow;

    public CallView(CallViewModel vm, MainWindow window)
    {
        _vm = vm;
        _window = window;
        RequestedTheme = ElementTheme.Dark;
        Background = Brush(0x0B0B0F);
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ---- Stage --------------------------------------------------------------------------
        _remote.FrameSizeChanged += (_, _) => ReportRemoteLayout();
        _remote.SizeChanged += (_, _) => ReportRemoteLayout();
        _remote.DoubleTapped += (_, _) => _vm.ToggleFit();
        _stage.Children.Add(_remote);
        _stage.Children.Add(_placeholder);
        // Group call: the others as a grid between the top bar and the toolbar (always shown there).
        _groupStage = new GroupStage(vm) { Margin = GroupStageMargin(pip: false) };
        _stage.Children.Add(_groupStage);

        _roomRow = Row(8,
            Text("Room", 14, foreground: White(0xB0)).With(t => t.VerticalAlignment = VerticalAlignment.Center),
            Text(_vm.RoomId, 22, FontWeights.SemiBold, White()).With(t =>
            {
                t.IsTextSelectionEnabled = true;
                t.VerticalAlignment = VerticalAlignment.Center;
            }),
            CopyButton());
        _roomRow.HorizontalAlignment = HorizontalAlignment.Center;
        // Alone, the self view is the stage behind it.
        _local.Attach(_stage);
        _stage.Children.Add(_local);
        // Above the toolbar, as on the web, so it doesn't cover your face.
        _waiting = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(32, 28, 32, 28),
            Margin = WaitingMargin(pip: false),
            CornerRadius = new CornerRadius(16),
            Background = Brush(0x1E1E24, 0xE6),
            BorderBrush = White(0x1A),
            BorderThickness = new Thickness(1),
            MaxWidth = 440,
            Child = Column(14,
                _waitingRing,
                _waitingTitle.With(t => t.TextAlignment = TextAlignment.Center),
                _waitingSubtitle.With(t => t.TextAlignment = TextAlignment.Center),
                _roomRow),
        };
        _stage.Children.Add(_waiting);

        // ---- Top bar ------------------------------------------------------------------------
        _timerChip = Chip(Icon("\uE916", 13).With(i => i.Foreground = Brush(0x6CCB5F)), _timer);
        _e2eeChip = Chip(Icon(Glyphs.Lock, 13).With(i => i.Foreground = White()), Text("End-to-end encrypted", 13, foreground: White()))
            .Tip(vm.IsGroup ? "Media is encrypted with a key only the people in this call have" : "Media is encrypted with a key only the two of you have");
        _people = new Button
        {
            Content = Row(6, Icon(Glyphs.People, 13), _peopleText),
            Background = Black(0x73),
            Foreground = White(),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Flyout = (_peopleFlyout = new PeopleFlyout(vm)).Flyout,
        };
        _people.Resources["ButtonBackgroundPointerOver"] = White(0x2E);
        _people.Resources["ButtonBackgroundPressed"] = White(0x14);
        _people.Resources["ButtonForegroundPointerOver"] = White();
        _people.Resources["ButtonForegroundPressed"] = White(0xCC);
        _people.Flyout.Opened += (_, _) => _openFlyouts++;
        _people.Flyout.Closed += (_, _) => _openFlyouts = Math.Max(0, _openFlyouts - 1);
        var stopSharing = new HyperlinkButton { Content = "Stop", Foreground = Brush(0xFF99A4), Padding = new Thickness(6, 0, 0, 0) };
        stopSharing.Click += (_, _) => _ = _vm.StopSharingAsync();
        _sharingChip = Chip(Icon(Glyphs.Screen, 13).With(i => i.Foreground = Brush(0x99C2FF)), _sharingText, stopSharing);
        _topBar.Children.Add(Row(8,
            Chip(Text("Room " + _vm.RoomId, 13, FontWeights.SemiBold, White())).Tip("Room ID"),
            _e2eeChip, _timerChip, _people));
        _topBar.Children.Add(_sharingChip.With(c => c.HorizontalAlignment = HorizontalAlignment.Center));
        _pip = ToolButton(Glyphs.PictureInPicture, "Picture in picture (P)", small: true);
        _pip.Click += (_, _) => _window.TogglePictureInPicture();
        _topBar.Children.Add(_pip.With(b => b.HorizontalAlignment = HorizontalAlignment.Right));
        _stage.Children.Add(_topBar);

        // ---- Toolbar ------------------------------------------------------------------------
        _mic = ToolButton(Glyphs.Mic, "Mute (M)");
        _mic.Click += (_, _) => _vm.ToggleMic();
        _camera = ToolButton(Glyphs.Camera, "Turn camera off (V)");
        _camera.Click += (_, _) => _vm.ToggleCamera();
        _effects = ToolButton(Glyphs.Background, "Backgrounds and effects (B)");
        _effects.Click += (_, _) => ToggleEffects();
        _share = ToolButton(Glyphs.Screen, "Share");
        _share.Flyout = ShareMenu();
        _chatButton = ToolButton(Glyphs.Chat, "Chat (C)");
        _chatButton.Click += (_, _) => ToggleChat();
        _chatHost = new Grid { Children = { _chatButton, _unread } };
        _more = ToolButton(Glyphs.More, "More options");
        _more.Flyout = MoreMenu();
        _leave = new Button
        {
            Content = Icon(Glyphs.HangUp, 20),
            Width = 64,
            Height = 48,
            CornerRadius = new CornerRadius(24),
            Background = Brush(0xC42B1C),
            Foreground = White(),
            BorderThickness = new Thickness(0),
        }.Tip("Leave");
        _leave.Resources["ButtonBackgroundPointerOver"] = Brush(0xD13438);
        _leave.Resources["ButtonBackgroundPressed"] = Brush(0xA4262C);
        _leave.Resources["ButtonForegroundPointerOver"] = White();
        _leave.Resources["ButtonForegroundPressed"] = White();
        _leave.Click += (_, _) => _vm.Leave();
        _exitPip = ToolButton(Glyphs.PictureInPicture, "Back to the full window (P)");
        _exitPip.Click += (_, _) => _window.TogglePictureInPicture();
        _exitPip.Visibility = Visibility.Collapsed;
        _toolbar = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 20),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(34),
            Background = Brush(0x202026, 0xE6),
            BorderBrush = White(0x1A),
            BorderThickness = new Thickness(1),
            Translation = new System.Numerics.Vector3(0, 0, 32),
            Shadow = new ThemeShadow(),
            Child = Row(10, _mic, _camera, _effects, _share, _chatHost, _more, _exitPip, _leave),
        };
        _stage.Children.Add(_toolbar);
        foreach (var bar in new FrameworkElement[] { _toolbar, _topBar })
        {
            bar.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(250) };
            bar.PointerEntered += (_, _) => _pointerOverControls = true;
            bar.PointerExited += (_, _) => _pointerOverControls = false;
        }

        Children.Add(_stage);
        _chat = new ChatPanel(vm);
        Children.Add(_chat.Grid(column: 1));
        _effectsPanel = new EffectsPanel(vm);
        Children.Add(_effectsPanel.Grid(column: 1));

        // ---- Auto-hide and input -------------------------------------------------------------
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = ControlsTimeout;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideControlsIfIdle();
        _stage.PointerMoved += (_, _) => ShowControls();
        _stage.PointerPressed += (_, _) => ShowControls();
        _stage.PointerExited += (_, e) =>
        {
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse) _hideTimer.Interval = TimeSpan.FromSeconds(1);
        };
        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        AddAccelerator(VirtualKey.M, () => _vm.ToggleMic());
        AddAccelerator(VirtualKey.V, () => _vm.ToggleCamera());
        AddAccelerator(VirtualKey.C, ToggleChat);
        // Same keys and behaviour as the web client: B only opens, F and P need the other person,
        // Esc closes the open panel. Group calls have no F (each tile switches with a double-click)
        // and P shows the featured participant.
        AddAccelerator(VirtualKey.B, OpenEffects);
        AddAccelerator(VirtualKey.F, () => { if (_vm.CanToggleFit && _vm.PeersConnected) _vm.ToggleFit(); });
        AddAccelerator(VirtualKey.P, () => { if (HasRemote || _window.IsPictureInPicture) _window.TogglePictureInPicture(); });
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            if (!_vm.EffectsOpen && !_vm.ChatOpen) return;
            e.Handled = true;
            if (_vm.EffectsOpen) _vm.SetEffectsOpen(false);
            else _vm.SetChatOpen(false);
        };
        KeyboardAccelerators.Add(escape);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Loaded += (_, _) =>
        {
            // Accelerators fire for the focused element's ancestors; start inside the call screen.
            _mic.Focus(FocusState.Programmatic);
            ShowControls();
        };

        _window.PictureInPictureChanged += OnPictureInPictureChanged;
        Bind();
    }

    /// <summary>Someone to show: the peer of a 1:1 call, or anyone else in a group call.</summary>
    private bool HasRemote => _vm.IsGroup ? _vm.Phase == CallPhase.Connected : _vm.PeersConnected;

    private static Thickness GroupStageMargin(bool pip) => pip ? new Thickness(0) : new Thickness(16, TopInset, 16, BottomInset);

    private static Thickness WaitingMargin(bool pip) => pip ? new Thickness(8, 8, 8, 86) : new Thickness(24, 24, 24, BottomInset + 12);

    // ---- Bindings ---------------------------------------------------------------------------

    private void Bind()
    {
        _bindings.Observe(_vm, () =>
        {
            var phase = _vm.Phase;
            _waiting.Visibility = Visible(phase is CallPhase.Waiting or CallPhase.Connecting);
            _waitingRing.IsActive = phase == CallPhase.Connecting;
            _waitingRing.Visibility = Visible(phase == CallPhase.Connecting);
            _waitingTitle.Text = phase == CallPhase.Connecting ? "Connecting…" : "Waiting for someone to join";
            _roomRow.Visibility = Visible(phase == CallPhase.Waiting);
            UpdateWaitingSubtitle();
            _local.SetWholeStage(!HasRemote);
            UpdateRemote();
            UpdateClock();
            if (phase != CallPhase.Connected) ShowControls();
            else RestartHideTimer();
            // Leaving picture in picture when the last other person leaves: there is nobody to show.
            if (_vm.IsGroup && phase != CallPhase.Connected && _window.IsPictureInPicture) _window.TogglePictureInPicture();
        }, nameof(CallViewModel.Phase), nameof(CallViewModel.PeersConnected));

        _bindings.Observe(_vm, () =>
        {
            var group = _vm.IsGroup;
            _groupStage.Visibility = Visible(group);
            _people.Visibility = Visible(group && _vm.GroupJoined);
            _peopleText.Text = $"{_vm.PeopleCount} in call";
            ToolTipService.SetToolTip(_people, $"{_vm.PeopleCount} in call, show everyone");
            _local.SetLabel(group && _vm.GroupJoined ? _vm.SelfLabel : null);
        }, nameof(CallViewModel.IsGroup), nameof(CallViewModel.GroupJoined), nameof(CallViewModel.PeopleCount), nameof(CallViewModel.SelfLabel));
        _bindings.Observe(_vm, UpdateGroupPictureInPicture, nameof(CallViewModel.FeaturedParticipant));

        _bindings.Observe(_vm, UpdateRemote,
            nameof(CallViewModel.RemoteVideo), nameof(CallViewModel.HasRemoteVideo), nameof(CallViewModel.ShowRemotePlaceholder),
            nameof(CallViewModel.RemoteVideoHidden), nameof(CallViewModel.RemoteMedia), nameof(CallViewModel.PlaceholderTitle),
            nameof(CallViewModel.PlaceholderSubtitle));
        _bindings.Observe(_vm, () => _remote.Fit = _vm.RemoteFit, nameof(CallViewModel.RemoteFit));
        _bindings.Observe(_vm, () => _placeholder.SetSnapshot(_vm.RemoteSnapshot), nameof(CallViewModel.RemoteSnapshot));
        _bindings.Observe(_vm, () => _placeholder.SetLevel(_vm.RemoteAudioLevel), nameof(CallViewModel.RemoteAudioLevel));
        _bindings.Observe(_vm, () => _placeholder.SetSeed(_vm.AvatarSeed), nameof(CallViewModel.AvatarSeed));
        _bindings.Observe(_vm, () => _local.SetMicLevel(_vm.MicLevel), nameof(CallViewModel.MicLevel));

        _bindings.Observe(_vm, () =>
        {
            _local.SetFeed(_vm.LocalVideo, _vm.LocalMirrored, _vm.CameraOn || _vm.IsPresenting);
            _local.SetMicOn(_vm.MicOn);
            SetToggle(_mic, _vm.MicOn ? Glyphs.Mic : Glyphs.MicOff, _vm.MicOn ? "Mute (M)" : "Unmute (M)", off: !_vm.MicOn);
            SetToggle(_camera, _vm.CameraOn ? Glyphs.Camera : Glyphs.VideoOff,
                _vm.IsPresenting ? "The camera is off while you share" : _vm.CameraOn ? "Turn camera off (V)" : "Turn camera on (V)",
                off: !_vm.CameraOn && !_vm.IsPresenting);
            _camera.IsEnabled = !_vm.IsPresenting;
            SetToggle(_share, _vm.IsPresenting ? Glyphs.StopShare : Glyphs.Screen, _vm.IsPresenting ? "Sharing" : "Share", active: _vm.IsPresenting);
            _sharingChip.Visibility = Visible(_vm.IsPresenting);
            _sharingText.Text = _vm.Sharing == Presentation.File ? $"Playing {_vm.SharingTitle}" : $"Sharing {_vm.SharingTitle}";
        }, nameof(CallViewModel.LocalVideo), nameof(CallViewModel.LocalMirrored), nameof(CallViewModel.CameraOn),
            nameof(CallViewModel.MicOn), nameof(CallViewModel.Sharing), nameof(CallViewModel.SharingTitle), nameof(CallViewModel.IsPresenting));

        _bindings.Observe(_vm, () =>
        {
            var open = _vm.ChatOpen && !_window.IsPictureInPicture;
            _chat.Visibility = Visible(open);
            SetToggle(_chatButton, Glyphs.Chat, open ? "Close chat (C)" : "Chat (C)", active: open);
            _unread.Value = _vm.Unread;
            _unread.Visibility = Visible(_vm.Unread > 0 && !open);
            if (open) _chat.FocusInput();
        }, nameof(CallViewModel.ChatOpen), nameof(CallViewModel.Unread));

        _bindings.Observe(_vm, () =>
        {
            var open = _vm.EffectsOpen && !_window.IsPictureInPicture;
            // Collapsing the panel drops focus from its tiles; keep it in the call screen so the shortcuts work.
            var refocus = _effectsPanelShown && !open && FocusIsIn(_effectsPanel);
            _effectsPanelShown = open;
            _effectsPanel.Visibility = Visible(open);
            SetToggle(_effects, Glyphs.Background,
                _vm.IsPresenting ? "Backgrounds and effects are off while you share" : open ? "Close backgrounds and effects (Esc)" : "Backgrounds and effects (B)",
                active: open);
            _effects.IsEnabled = _vm.CanOpenEffects;
            if (refocus) ((Control)(_effects.IsEnabled ? _effects : _mic)).Focus(FocusState.Programmatic);
        }, nameof(CallViewModel.EffectsOpen), nameof(CallViewModel.CanOpenEffects), nameof(CallViewModel.IsPresenting));

        _bindings.Observe(_vm, () => _e2eeChip.Visibility = Visible(_vm.E2ee), nameof(CallViewModel.E2ee));
        _bindings.Add(() => _window.PictureInPictureChanged -= OnPictureInPictureChanged);
    }

    private void UpdateRemote()
    {
        var showVideo = !_vm.IsGroup && _vm.HasRemoteVideo && !_vm.ShowRemotePlaceholder;
        _remote.Feed = showVideo ? _vm.RemoteVideo : null;
        _remote.Visibility = Visible(showVideo);
        _placeholder.Visibility = Visible(!_vm.IsGroup && _vm.ShowRemotePlaceholder);
        _placeholder.SetText(_vm.PlaceholderTitle, _vm.PlaceholderSubtitle);
    }

    private void UpdateWaitingSubtitle()
    {
        if (_vm.IsGroup)
        {
            _waitingSubtitle.Text = _vm.Phase == CallPhase.Connecting
                ? "Joining the group call server"
                : _vm.E2ee
                    ? "Open the app on other devices, choose Group call (SFU) and join this room. Everyone must turn on end-to-end encryption too."
                    : "Open the app on other devices, choose Group call (SFU) and join this room.";
            return;
        }
        _waitingSubtitle.Text = _vm.Phase == CallPhase.Connecting
            ? "Setting up a direct connection"
            : _vm.E2ee
                ? "Share this room ID. They need to turn on end-to-end encryption too."
                : "Share this room ID with the other person.";
    }

    /// <summary>
    /// Picture in picture in a group call shows one participant, as the web does: the active
    /// speaker, else the first with video, else the first. A grid of tiles at that size would
    /// show nobody well.
    /// </summary>
    private void UpdateGroupPictureInPicture()
    {
        var pip = _vm.IsGroup && _window.IsPictureInPicture;
        _groupStage.ShowOnly(pip, _vm.FeaturedParticipant);
        _groupStage.Margin = GroupStageMargin(pip);
        _groupStage.Gap = pip ? 0 : 12;
    }

    private void UpdateClock()
    {
        _timerChip.Visibility = Visible(_vm.ConnectedSince != null);
        if (_vm.ConnectedSince is not { } since) return;
        var elapsed = DateTimeOffset.UtcNow - since;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        _timer.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private void ReportRemoteLayout()
    {
        if (_remote.FrameWidth > 0) _vm.ReportRemoteLayout(_remote.FrameWidth, _remote.FrameHeight, _remote.ActualWidth, _remote.ActualHeight);
    }

    // ---- Controls ---------------------------------------------------------------------------

    private void ShowControls()
    {
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            foreach (var bar in new UIElement[] { _toolbar, _topBar })
            {
                bar.Opacity = 1;
                bar.IsHitTestVisible = true;
            }
            UpdateTileInsets();
        }
        _hideTimer.Interval = ControlsTimeout;
        RestartHideTimer();
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideControlsIfIdle()
    {
        var typing = FocusManager.GetFocusedElement(XamlRoot) is TextBox;
        // The group grid sits between the bars (as on the web), so they stay unless in picture in picture.
        var groupStage = _vm.IsGroup && !_window.IsPictureInPicture;
        if (_vm.Phase != CallPhase.Connected || _openFlyouts > 0 || _pointerOverControls || typing || groupStage)
        {
            RestartHideTimer();
            return;
        }
        _controlsVisible = false;
        foreach (var bar in new UIElement[] { _toolbar, _topBar })
        {
            bar.Opacity = 0;
            bar.IsHitTestVisible = false;
        }
        UpdateTileInsets();
    }

    private void UpdateTileInsets()
    {
        var pip = _window.IsPictureInPicture;
        _local.SetInsets(_controlsVisible && !pip ? new Thickness(0, TopInset, 0, BottomInset) : new Thickness(0));
    }

    private void ToggleChat()
    {
        if (_window.IsPictureInPicture) return;
        _vm.ToggleChat();
    }

    // Opening is refused while presenting (the view model checks); closing always works.
    private void ToggleEffects()
    {
        if (_window.IsPictureInPicture) return;
        _vm.ToggleEffects();
    }

    private void OpenEffects()
    {
        if (_window.IsPictureInPicture) return;
        _vm.OpenEffects();
    }

    private bool FocusIsIn(UIElement container)
    {
        if (XamlRoot == null) return false;
        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; element != null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == container) return true;
        }
        return false;
    }

    private void OnPictureInPictureChanged()
    {
        var pip = _window.IsPictureInPicture;
        _local.Visibility = Visible(!pip);
        _topBar.Visibility = Visible(!pip);
        _share.Visibility = Visible(!pip);
        _chatHost.Visibility = Visible(!pip);
        _effects.Visibility = Visible(!pip);
        _more.Visibility = Visible(!pip);
        _exitPip.Visibility = Visible(pip);
        _waiting.Padding = pip ? new Thickness(16) : new Thickness(32, 28, 32, 28);
        _waiting.Margin = WaitingMargin(pip);
        _toolbar.Margin = new Thickness(0, 0, 0, pip ? 8 : 20);
        _chat.Visibility = Visible(_vm.ChatOpen && !pip);
        _effectsPanel.Visibility = Visible(_vm.EffectsOpen && !pip);
        UpdateGroupPictureInPicture();
        UpdateTileInsets();
        ShowControls();
    }

    private void AddAccelerator(VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, e) =>
        {
            // Plain letters belong to the text box being typed in.
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
            e.Handled = true;
            action();
            ShowControls();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private Button CopyButton()
    {
        var button = new Button { Content = Row(6, Icon(Glyphs.Copy, 14), Text("Copy", 13)), Padding = new Thickness(10, 4, 10, 4) }.Tip("Copy the room ID");
        button.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(_vm.RoomId);
            Clipboard.SetContent(data);
            _window.ShowToast(new Toast("Room ID copied", ToastKind.Success, Glyphs.Copy));
        };
        return button;
    }

    // ---- Menus ------------------------------------------------------------------------------

    private MenuFlyout ShareMenu()
    {
        var menu = TrackedMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add(MenuItem("Share your screen…", Glyphs.Screen, () => _ = SharePicker.ShowAsync(_vm, XamlRoot, DesktopSourceType.Screen)));
            menu.Items.Add(MenuItem("Share a window…", Glyphs.Window, () => _ = SharePicker.ShowAsync(_vm, XamlRoot, DesktopSourceType.Window)));
            if (_vm.CanShareFile) menu.Items.Add(MenuItem("Share a video file…", Glyphs.Film, () => _ = SharePicker.ShareFileAsync(_vm, _window)));
            if (_vm.IsPresenting)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MenuItem("Stop sharing", Glyphs.StopShare, () => _ = _vm.StopSharingAsync()));
            }
        };
        return menu;
    }

    private MenuFlyout MoreMenu()
    {
        var menu = TrackedMenu();
        menu.Opening += (_, _) =>
        {
            _vm.RefreshDevices();
            menu.Items.Clear();
            if (_vm.CanSwitchCamera) menu.Items.Add(MenuItem("Switch camera", Glyphs.Switch, () => _ = _vm.SwitchCameraAsync()));
            menu.Items.Add(DeviceMenu("Camera", Glyphs.Camera, _vm.Cameras, _vm.CameraId, id => _ = _vm.SelectCameraAsync(id), !_vm.IsPresenting));
            menu.Items.Add(DeviceMenu("Microphone", Glyphs.Mic, _vm.Microphones, _vm.MicrophoneId, _vm.SelectMicrophone, true));
            menu.Items.Add(DeviceMenu("Speaker", Glyphs.Speaker, _vm.Speakers, _vm.SpeakerId, _vm.SelectSpeaker, true));
            menu.Items.Add(new MenuFlyoutSeparator());

            // Local only. In a group call they apply to everyone, also people who join later, so
            // they work before anyone is there.
            var muteThem = new ToggleMenuFlyoutItem
            {
                Text = _vm.IsGroup ? "Mute everyone" : "Mute their audio",
                Icon = new FontIcon { Glyph = Glyphs.SpeakerOff },
                IsChecked = _vm.RemoteAudioMuted,
                IsEnabled = _vm.IsGroup || _vm.PeersConnected,
            };
            muteThem.Click += (_, _) => _vm.ToggleRemoteAudio();
            menu.Items.Add(muteThem);
            var hideThem = new ToggleMenuFlyoutItem
            {
                Text = _vm.IsGroup ? "Hide everyone's video" : "Hide their video",
                Icon = new FontIcon { Glyph = Glyphs.Hide },
                IsChecked = _vm.RemoteVideoHidden,
                IsEnabled = _vm.IsGroup || _vm.PeersConnected,
            };
            hideThem.Click += (_, _) => _vm.ToggleRemoteVideo();
            menu.Items.Add(hideThem);
            if (_vm.CanToggleFit)
            {
                menu.Items.Add(MenuItem(_vm.RemoteFit == VideoFit.Fill ? "Fit video to window" : "Fill the window", _vm.RemoteFit == VideoFit.Fill ? Glyphs.Fit : Glyphs.Fill,
                    _vm.ToggleFit, "F").With(i => i.IsEnabled = _vm.PeersConnected));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("Backgrounds and effects", Glyphs.Background, OpenEffects, "B").With(i =>
                i.IsEnabled = _vm.CanOpenEffects));
            menu.Items.Add(MenuItem(_vm.IsGroup ? "Picture in picture (active speaker)" : "Picture in picture", Glyphs.PictureInPicture,
                _window.TogglePictureInPicture, "P").With(i => i.IsEnabled = HasRemote));
        };
        return menu;
    }

    private static MenuFlyoutSubItem DeviceMenu(string title, string glyph, IReadOnlyList<MediaDevice> devices, string? selected, Action<string> select, bool enabled)
    {
        var sub = new MenuFlyoutSubItem { Text = title, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled && devices.Count > 0 };
        foreach (var device in devices)
        {
            var item = new RadioMenuFlyoutItem { Text = device.Name, GroupName = title, IsChecked = device.Id == selected };
            item.Click += (_, _) => select(device.Id);
            sub.Items.Add(item);
        }
        if (devices.Count == 0) sub.Items.Add(new MenuFlyoutItem { Text = "None found", IsEnabled = false });
        return sub;
    }

    private MenuFlyout TrackedMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        menu.Opened += (_, _) => _openFlyouts++;
        menu.Closed += (_, _) =>
        {
            _openFlyouts = Math.Max(0, _openFlyouts - 1);
            RestartHideTimer();
        };
        return menu;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action action, string? shortcut = null)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (shortcut != null) item.KeyboardAcceleratorTextOverride = shortcut;
        item.Click += (_, _) => action();
        return item;
    }

    // ---- Building blocks --------------------------------------------------------------------

    private static Button ToolButton(string glyph, string tip, bool small = false)
    {
        var size = small ? 40 : 48;
        var button = new Button
        {
            Content = Icon(glyph, small ? 16 : 18),
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(size / 2.0),
            Background = White(0x1A),
            Foreground = White(),
            BorderThickness = new Thickness(0),
        }.Tip(tip);
        button.Resources["ButtonBackgroundPointerOver"] = White(0x2E);
        button.Resources["ButtonBackgroundPressed"] = White(0x14);
        button.Resources["ButtonForegroundPointerOver"] = White();
        button.Resources["ButtonForegroundPressed"] = White(0xCC);
        return button;
    }

    /// <summary>Red when something is off (muted, camera off), blue when a mode is on.</summary>
    private static void SetToggle(Button button, string glyph, string tip, bool off = false, bool active = false)
    {
        ((FontIcon)button.Content).Glyph = glyph;
        button.Tip(tip);
        AutomationProperties.SetName(button, tip);
        var background = off ? Brush(0xC42B1C) : active ? Brush(0x3B6FE0) : White(0x1A);
        var hover = off ? Brush(0xD13438) : active ? Brush(0x4A7DF0) : White(0x2E);
        button.Background = background;
        button.Resources["ButtonBackgroundPointerOver"] = hover;
    }

    private static Border Chip(params UIElement[] content) => new()
    {
        Background = Black(0x73),
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(12, 6, 12, 6),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Row(6, content).With(r => r.VerticalAlignment = VerticalAlignment.Center),
    };

    public void Dispose()
    {
        _peopleFlyout.Dispose();
        _bindings.Dispose();
        _hideTimer.Stop();
        _clockTimer.Stop();
        _remote.Feed = null;
        _local.SetFeed(null, false, false);
        _groupStage.Dispose();
        _chat.Dispose();
        _effectsPanel.Dispose();
    }
}