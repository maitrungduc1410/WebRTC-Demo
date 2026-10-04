using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Group;
using WebRtcDemo.Core.Signaling;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>Everyone in a group call (web: PeopleDialog.vue): you first and highlighted, then join order.</summary>
internal sealed class PeopleFlyout : IDisposable
{
    private static readonly HashSet<string> WatchedCallProperties =
    [
        nameof(CallViewModel.PeopleCount), nameof(CallViewModel.ActiveSpeakerId), nameof(CallViewModel.MicOn),
        nameof(CallViewModel.CameraOn), nameof(CallViewModel.IsPresenting), nameof(CallViewModel.SelfLabel),
    ];

    private PeopleList? _list;
    private bool _disposed;

    public PeopleFlyout(CallViewModel vm)
    {
        Flyout.Opening += (_, _) =>
        {
            CloseList();
            if (_disposed) return;
            _list = new PeopleList(vm);
            Flyout.Content = _list.Root;
        };
        Flyout.Closed += (_, _) => CloseList();
    }

    public Flyout Flyout { get; } = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };

    private void CloseList()
    {
        _list?.Dispose();
        _list = null;
        Flyout.Content = null;
    }

    /// <summary>The call is over: closes the list now rather than when the flyout's Closed comes.</summary>
    public void Dispose()
    {
        _disposed = true;
        Flyout.Hide();
        CloseList();
    }

    /// <summary>The open list; it follows the call until disposed.</summary>
    private sealed class PeopleList : IDisposable
    {
        private readonly CallViewModel _vm;
        private readonly TextBlock _count;
        private readonly StackPanel _rows = new() { Spacing = 4 };
        private const string SelfKey = "";
        private readonly List<GroupParticipant> _watched = [];
        private readonly Dictionary<string, PersonRow> _people = [];

        public PeopleList(CallViewModel vm)
        {
            _vm = vm;
            _count = Text(string.Empty, 18, FontWeights.SemiBold);
            Root = new StackPanel
            {
                Width = 320,
                Spacing = 12,
                Children =
                {
                    Column(2,
                        _count,
                        Text("Others see you by the name on your row.", 13, foreground: ThemeBrush("TextFillColorSecondaryBrush"))),
                    new ScrollViewer { MaxHeight = 420, Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                },
            };
            vm.PropertyChanged += OnCallChanged;
            vm.Participants.CollectionChanged += OnParticipantsChanged;
            Refresh();
        }

        public StackPanel Root { get; }

        private void OnCallChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is { } name && WatchedCallProperties.Contains(name)) Refresh();
        }

        private void OnParticipantsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

        private void OnParticipantChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GroupParticipant.State)) Refresh();
        }

        private void Refresh()
        {
            Unwatch();
            _count.Text = $"{_vm.PeopleCount} in call";
            var presenting = _vm.IsPresenting;
            var keys = new List<string> { SelfKey };
            Row(SelfKey, _vm.SelfLabel, Avatar.LocalSeed, you: true)
                .Update(new MediaState(_vm.MicOn, presenting || _vm.CameraOn, presenting), speaking: false);
            foreach (var participant in _vm.Participants)
            {
                participant.PropertyChanged += OnParticipantChanged;
                _watched.Add(participant);
                keys.Add(participant.Id);
                Row(participant.Id, participant.Label, participant.AvatarSeed, you: false)
                    .Update(participant.State, participant.Id == _vm.ActiveSpeakerId);
            }
            foreach (var gone in _people.Keys.Except(keys).ToList()) _people.Remove(gone);
            // Only rows that leave or move are taken out, so the others keep animating their marks.
            var order = keys.Select(k => _people[k].Root).ToList();
            for (var i = _rows.Children.Count - 1; i >= 0; i--)
            {
                if (!order.Contains(_rows.Children[i])) _rows.Children.RemoveAt(i);
            }
            for (var i = 0; i < order.Count; i++)
            {
                if (i < _rows.Children.Count && ReferenceEquals(_rows.Children[i], order[i])) continue;
                _rows.Children.Remove(order[i]);
                _rows.Children.Insert(i, order[i]);
            }
        }

        private PersonRow Row(string key, string label, string seed, bool you)
        {
            if (!_people.TryGetValue(key, out var row) || row.Label != label)
            {
                row = new PersonRow(label, seed, you);
                _people[key] = row;
            }
            return row;
        }

        private void Unwatch()
        {
            foreach (var participant in _watched) participant.PropertyChanged -= OnParticipantChanged;
            _watched.Clear();
        }

        public void Dispose()
        {
            _vm.PropertyChanged -= OnCallChanged;
            _vm.Participants.CollectionChanged -= OnParticipantsChanged;
            Unwatch();
        }
    }

    /// <summary>One row: avatar with a speaking ring, the label, and the status mark.</summary>
    private sealed class PersonRow
    {
        private readonly Ellipse _speaking = new()
        {
            Stroke = Brush(0x34D399),
            StrokeThickness = 2,
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) },
        };
        private readonly FontIcon _cameraOff;
        private readonly StatusSlot _status = new(ThemeBrush("TextFillColorPrimaryBrush"));

        public PersonRow(string label, string seed, bool you)
        {
            Label = label;
            var (start, end) = Avatar.Colors(seed);
            var avatar = new Grid
            {
                Width = 36,
                Height = 36,
                Children =
                {
                    new Ellipse { Fill = you ? Gradient(0x6366F1, 0x8B5CF6) : Gradient(start, end) },
                    Text(label.Length > 0 ? label[..1].ToUpperInvariant() : "?", 14, FontWeights.SemiBold, White()).With(t =>
                    {
                        t.HorizontalAlignment = HorizontalAlignment.Center;
                        t.VerticalAlignment = VerticalAlignment.Center;
                    }),
                    _speaking,
                },
            };
            _cameraOff = Icon(Glyphs.VideoOff, 14).With(i =>
            {
                i.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
                i.VerticalAlignment = VerticalAlignment.Center;
            }).Tip("Camera off");
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(avatar);
            row.Children.Add(Row(6, _status, Text(label, 14, FontWeights.SemiBold).With(t =>
            {
                t.TextWrapping = TextWrapping.NoWrap;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.VerticalAlignment = VerticalAlignment.Center;
            })).With(r => r.VerticalAlignment = VerticalAlignment.Center).Grid(column: 1));
            if (you)
            {
                row.Children.Add(new Border
                {
                    Background = Brush(0x6366F1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(8, 2, 8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = Text("You", 11, FontWeights.SemiBold, White()),
                }.Grid(column: 2));
            }
            row.Children.Add(_cameraOff.Grid(column: 3));

            Root = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(8),
                Background = you ? Brush(0x6366F1, 0x26) : Transparent,
                BorderBrush = you ? Brush(0x818CF8, 0x66) : Transparent,
                BorderThickness = new Thickness(1),
                Child = row,
            };
        }

        public string Label { get; }
        public Border Root { get; }

        public void Update(MediaState state, bool speaking)
        {
            _status.Status = StatusSlot.For(state.Audio, state.Screen);
            _cameraOff.Visibility = Visible(!state.Video && !state.Screen);
            _speaking.Opacity = speaking ? 1 : 0;
        }
    }
}
