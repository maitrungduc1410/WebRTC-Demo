using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using WebRtcDemo.App.Ui;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Lobby;
using WebRtcDemo.Core.Signaling;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>Room id, E2EE switch, join, and the signaling server with its live status.</summary>
internal sealed class LobbyView : Grid
{
    private readonly LobbyViewModel _vm;
    private readonly Bindings _bindings = new();
    private readonly TextBox _room;

    public LobbyView(LobbyViewModel vm)
    {
        _vm = vm;
        Padding = new Thickness(24, 0, 24, 24);

        var title = Text("Start a call", 28, FontWeights.SemiBold);
        var subtitle = Text("Join the same room on another device: web, iOS, Android, macOS or Windows.", 14,
            foreground: ThemeBrush("TextFillColorSecondaryBrush"));

        // 1:1 through the signaling server, or a group call through the SFU (as the web's mode switch).
        var oneToOne = new SelectorBarItem { Text = "1:1 call", Icon = new FontIcon { Glyph = Glyphs.Person }, IsSelected = !_vm.GroupMode };
        var group = new SelectorBarItem { Text = "Group call (SFU)", Icon = new FontIcon { Glyph = Glyphs.People }, IsSelected = _vm.GroupMode };
        var mode = new SelectorBar { Items = { oneToOne, group } };
        mode.SelectionChanged += (_, _) => _vm.GroupMode = mode.SelectedItem == group;

        _room = new TextBox
        {
            Header = "Room ID",
            PlaceholderText = "123456",
            FontSize = 20,
            MaxLength = 32,
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
        };
        _room.TextChanged += (_, _) =>
        {
            if (_vm.RoomId != _room.Text) _vm.RoomId = _room.Text;
        };
        _room.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && _vm.JoinCommand.CanExecute(null))
            {
                _vm.JoinCommand.Execute(null);
                e.Handled = true;
            }
        };
        var newRoom = new Button { Content = Icon("\uE72C"), Command = _vm.NewRoomIdCommand, VerticalAlignment = VerticalAlignment.Bottom, Height = 40 }
            .Tip("New random room ID");
        var roomRow = new Grid { ColumnSpacing = 8 };
        roomRow.ColumnDefinitions.Add(new ColumnDefinition());
        roomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        roomRow.Children.Add(_room);
        roomRow.Children.Add(newRoom.Grid(column: 1));

        var e2ee = new ToggleSwitch { OnContent = "On", OffContent = "Off" };
        e2ee.Toggled += (_, _) => _vm.E2ee = e2ee.IsOn;
        var e2eeRow = new Grid { ColumnSpacing = 12 };
        e2eeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        e2eeRow.ColumnDefinitions.Add(new ColumnDefinition());
        e2eeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var e2eeHint = Text(string.Empty, 12, foreground: ThemeBrush("TextFillColorSecondaryBrush"));
        e2eeRow.Children.Add(Icon(Glyphs.Lock, 18).With(i => i.VerticalAlignment = VerticalAlignment.Center));
        e2eeRow.Children.Add(Column(2,
            Text("End-to-end encryption", 14, FontWeights.SemiBold),
            e2eeHint).Grid(column: 1));
        e2eeRow.Children.Add(e2ee.Grid(column: 2).With(t => t.MinWidth = 0));

        var joinText = Text(string.Empty, 15, FontWeights.SemiBold);
        var join = new Button
        {
            Content = Row(8, Icon(Glyphs.Camera), joinText),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 44,
            Command = _vm.JoinCommand,
        }.Tip("Join (Enter)");

        // The server the mode uses: signaling server or group call server
        var statusDot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        var statusText = Text(string.Empty, 13);
        var serverUrl = Text(string.Empty, 13, foreground: ThemeBrush("TextFillColorSecondaryBrush"));
        var serverLabel = Text(string.Empty, 14, FontWeights.SemiBold);
        var serverHelp = Text(string.Empty, 12, foreground: ThemeBrush("TextFillColorSecondaryBrush"));
        var address = new TextBox();
        var addressError = Text(string.Empty, 12, foreground: ThemeBrush("SystemFillColorCriticalBrush"));
        var connect = new Button { Content = "Connect" };
        void Apply()
        {
            if (_vm.ApplyServerAddress()) address.Text = _vm.ServerAddressText;
        }
        connect.Click += (_, _) => Apply();
        address.TextChanged += (_, _) => _vm.ServerAddressText = address.Text;
        address.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            Apply();
            e.Handled = true;
        };
        var addressRow = new Grid { ColumnSpacing = 8 };
        addressRow.ColumnDefinitions.Add(new ColumnDefinition());
        addressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addressRow.Children.Add(address);
        addressRow.Children.Add(connect.Grid(column: 1));

        var server = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Header = Row(10, statusDot, Column(0, serverLabel, Row(6, statusText, serverUrl))),
            Content = Column(8, serverHelp, addressRow, addressError),
        };

        var card = new Border
        {
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(28),
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = Column(20,
                Column(6, title, subtitle),
                mode,
                roomRow,
                e2eeRow,
                join,
                server),
        };
        Children.Add(new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _bindings.Observe(_vm, () =>
        {
            if (_room.Text != _vm.RoomId) _room.Text = _vm.RoomId;
        }, nameof(LobbyViewModel.RoomId));
        _bindings.Observe(_vm, () => e2ee.IsOn = _vm.E2ee, nameof(LobbyViewModel.E2ee));
        _bindings.Observe(_vm, () =>
        {
            joinText.Text = _vm.JoinText;
            e2eeHint.Text = _vm.E2eeHint + ". Video uses VP8 so every platform can decrypt it.";
            serverLabel.Text = _vm.ServerLabel;
            address.PlaceholderText = _vm.ServerPlaceholder;
            serverHelp.Text = _vm.GroupMode
                ? "Start sfu-server (go run .) on a computer every device can reach, then enter its address. Without a port, :4001 is used."
                : "Start signaling-server (npm run dev) on a computer every device can reach, then enter the address it prints.";
            if (oneToOne.IsSelected == _vm.GroupMode)
            {
                oneToOne.IsSelected = !_vm.GroupMode;
                group.IsSelected = _vm.GroupMode;
            }
        }, nameof(LobbyViewModel.GroupMode));
        _bindings.Observe(_vm, () =>
        {
            if (address.Text != _vm.ServerAddressText) address.Text = _vm.ServerAddressText;
            addressError.Text = _vm.ServerAddressError ?? string.Empty;
            addressError.Visibility = Visible(_vm.ServerAddressError != null);
        }, nameof(LobbyViewModel.ServerAddressText), nameof(LobbyViewModel.ServerAddressError));
        _bindings.Observe(_vm, () =>
        {
            statusText.Text = _vm.ServerStatusText;
            serverUrl.Text = "· " + _vm.ServerUrl;
            statusDot.Fill = _vm.ServerStatus switch
            {
                ServerStatus.Connected => ThemeBrush("SystemFillColorSuccessBrush"),
                ServerStatus.Unreachable => ThemeBrush("SystemFillColorCriticalBrush"),
                _ => ThemeBrush("SystemFillColorCautionBrush"),
            };
            // Open the server settings when it can't be reached: the likely fix is there.
            if (_vm.ServerStatus == ServerStatus.Unreachable) server.IsExpanded = true;
        }, nameof(LobbyViewModel.ServerStatus), nameof(LobbyViewModel.ServerStatusText), nameof(LobbyViewModel.ServerUrl));
    }

    public void FocusRoom() => DispatcherQueue.TryEnqueue(() =>
    {
        _room.Focus(FocusState.Programmatic);
        _room.SelectAll();
    });
}
