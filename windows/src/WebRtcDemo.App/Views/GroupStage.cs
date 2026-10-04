using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WebRtcDemo.App.Ui;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Group;
using Windows.Foundation;

namespace WebRtcDemo.App.Views;

/// <summary>
/// The others in a group call as an adaptive grid: the column count that gives the largest tiles
/// of roughly camera shape, every cell the same size and the last row centred (GroupCallView.vue).
/// In picture in picture only one participant shows, filling the window.
/// </summary>
internal sealed class GroupStage : Panel, IDisposable
{
    private readonly CallViewModel _vm;
    private readonly Bindings _bindings = new();
    private readonly Dictionary<GroupParticipant, GroupTile> _tiles = [];
    private GroupParticipant? _only;
    private bool _single;

    public GroupStage(CallViewModel vm)
    {
        _vm = vm;
        _bindings.Observe(vm.Participants, OnParticipantsChanged);
        Sync();
    }

    public double Gap { get; set; } = 12;

    /// <summary>Shows just <paramref name="participant"/> (picture in picture), or everyone with false.</summary>
    public void ShowOnly(bool single, GroupParticipant? participant)
    {
        if (_single == single && _only == participant) return;
        _single = single;
        _only = participant;
        foreach (var (p, tile) in _tiles) tile.Visibility = UiFactory.Visible(!single || p == participant);
        InvalidateMeasure();
    }

    private void OnParticipantsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void Sync()
    {
        var current = _vm.Participants.ToList();
        foreach (var (participant, tile) in _tiles.ToList())
        {
            if (current.Contains(participant)) continue;
            _tiles.Remove(participant);
            Children.Remove(tile);
            tile.Dispose();
        }
        for (var i = 0; i < current.Count; i++)
        {
            var participant = current[i];
            if (!_tiles.TryGetValue(participant, out var tile))
            {
                tile = new GroupTile(participant) { Opacity = 0 };
                _tiles[participant] = tile;
                tile.Visibility = UiFactory.Visible(!_single || participant == _only);
                var added = tile;
                tile.Loaded += (_, _) => added.Opacity = 1;
            }
            var index = Children.IndexOf(tile);
            if (index == i) continue;
            if (index >= 0) Children.RemoveAt(index);
            Children.Insert(Math.Min(i, Children.Count), tile);
        }
        InvalidateMeasure();
    }

    private List<GroupTile> Shown() => Children.OfType<GroupTile>().Where(t => t.Visibility == Visibility.Visible).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        var shown = Shown();
        var rects = GroupGrid.Layout(shown.Count, width, height, Gap);
        for (var i = 0; i < shown.Count; i++) shown[i].Measure(new Size(rects[i].Width, rects[i].Height));
        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Visible) child.Measure(new Size(0, 0));
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = Shown();
        var rects = GroupGrid.Layout(shown.Count, finalSize.Width, finalSize.Height, Gap);
        for (var i = 0; i < shown.Count; i++)
        {
            var r = rects[i];
            shown[i].Arrange(new Rect(r.X, r.Y, r.Width, r.Height));
        }
        return finalSize;
    }

    public void Dispose()
    {
        _bindings.Dispose();
        foreach (var tile in _tiles.Values) tile.Dispose();
        _tiles.Clear();
        Children.Clear();
    }
}
