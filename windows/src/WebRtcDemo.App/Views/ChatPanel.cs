using System.Collections.Specialized;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using WebRtcDemo.App.Ui;
using WebRtcDemo.Core.Call;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>Messages over the peer-to-peer data channel; nothing goes through the server.</summary>
internal sealed class ChatPanel : Grid, IDisposable
{
    public const double PanelWidth = 400;

    private readonly CallViewModel _vm;
    private readonly Bindings _bindings = new();
    private readonly StackPanel _messages = new() { Spacing = 6, Padding = new Thickness(16, 8, 16, 8) };
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _empty;
    private readonly TextBox _input;
    private readonly Button _send;

    public ChatPanel(CallViewModel vm)
    {
        _vm = vm;
        Width = PanelWidth;
        Background = Brush(0x1C1C21);
        BorderBrush = White(0x1A);
        BorderThickness = new Thickness(1, 0, 0, 0);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var close = new Button
        {
            Content = Icon("\uE711", 12),
            Background = Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
        }.Tip("Close chat (C)");
        close.Click += (_, _) => _vm.SetChatOpen(false);
        var header = new Grid { Padding = new Thickness(16, 12, 8, 12) };
        header.Children.Add(Column(2,
            Text("Chat", 18, FontWeights.SemiBold, White()),
            Text(vm.IsGroup ? "Relayed by the group call server, never stored" : "Peer-to-peer, never stored", 12, foreground: White(0x99))));
        header.Children.Add(close.With(c => c.VerticalAlignment = VerticalAlignment.Center));
        Children.Add(header);

        _messages.ChildrenTransitions = [new EntranceThemeTransition { FromVerticalOffset = 12 }];
        _scroller = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _empty = Text("No messages yet", 13, foreground: White(0x80)).With(t =>
        {
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
        });
        Children.Add(_scroller.Grid(row: 1));
        Children.Add(_empty.Grid(row: 1));

        _input = new TextBox { PlaceholderText = "Message", MaxLength = 2000, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            Send();
            e.Handled = true;
        };
        _input.TextChanged += (_, _) => UpdateSend();
        _send = new Button
        {
            Content = Icon(Glyphs.Send, 14),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 32,
        }.Tip("Send (Enter)");
        _send.Click += (_, _) => Send();
        var inputRow = new Grid { ColumnSpacing = 8, Padding = new Thickness(16, 8, 16, 16) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition());
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(_input);
        inputRow.Children.Add(_send.Grid(column: 1));
        Children.Add(inputRow.Grid(row: 2));

        foreach (var message in _vm.Messages) _messages.Children.Add(Bubble(message));
        _bindings.Observe(_vm.Messages, OnMessagesChanged);
        _bindings.Observe(_vm, () =>
        {
            _input.IsEnabled = _vm.ChatReady;
            _input.PlaceholderText = _vm.ChatReady ? "Message" : "Chat opens once you're connected";
            UpdateSend();
        }, nameof(CallViewModel.ChatReady));
        UpdateEmpty();
    }

    public void FocusInput() => DispatcherQueue.TryEnqueue(() => _input.Focus(FocusState.Programmatic));

    private void Send()
    {
        if (_vm.SendMessage(_input.Text)) _input.Text = string.Empty;
    }

    private void UpdateSend() => _send.IsEnabled = _vm.ChatReady && !string.IsNullOrWhiteSpace(_input.Text);

    private void UpdateEmpty() => _empty.Visibility = Visible(_messages.Children.Count == 0);

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (ChatMessage message in e.NewItems!) _messages.Children.Add(Bubble(message));
                break;
            case NotifyCollectionChangedAction.Remove:
                for (var i = 0; i < e.OldItems!.Count; i++) _messages.Children.RemoveAt(e.OldStartingIndex);
                break;
            default:
                _messages.Children.Clear();
                foreach (var message in _vm.Messages) _messages.Children.Add(Bubble(message));
                break;
        }
        UpdateEmpty();
        // After layout, so the new bubble's height counts.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _scroller.ChangeView(null, _scroller.ScrollableHeight, null, disableAnimation: false));
    }

    private static StackPanel Bubble(ChatMessage message)
    {
        var text = new TextBlock
        {
            Text = message.Text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = White(),
            FontSize = 14,
        };
        var clock = message.Timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        // Group calls name the sender of received messages ("Name · 1a2b").
        var time = Text(message.Sender != null ? $"{message.Sender} · {clock}" : clock, 11, foreground: White(0x80));
        time.HorizontalAlignment = message.IsLocal ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        return Column(2,
            new Border
            {
                Child = text,
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = message.IsLocal ? new CornerRadius(16, 16, 4, 16) : new CornerRadius(16, 16, 16, 4),
                Background = message.IsLocal ? Brush(0x3B6FE0) : Brush(0x2E2E35),
                MaxWidth = PanelWidth * 0.75,
                HorizontalAlignment = message.IsLocal ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            },
            time).With(c => c.HorizontalAlignment = message.IsLocal ? HorizontalAlignment.Right : HorizontalAlignment.Left);
    }

    public void Dispose() => _bindings.Dispose();
}
