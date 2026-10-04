using CommunityToolkit.Mvvm.ComponentModel;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Core.Signaling;

namespace WebRtcDemo.Core.Group;

/// <summary>Someone else in a group call: one tile of the stage and one row of the people list.</summary>
public sealed partial class GroupParticipant : ObservableObject
{
    private TileFit _fit;

    public GroupParticipant(string id, string name, MediaState state)
    {
        Id = id;
        Name = name;
        State = state;
        _fit.SetPresenting(state.Screen);
    }

    public string Id { get; }
    public string Name { get; }
    public string Label => GroupLabels.Label(Id, Name);
    public string AvatarSeed => Avatar.RemoteSeed(Id);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideo), nameof(MicOff), nameof(IsPresenting))]
    public partial MediaState State { get; private set; }

    /// <summary>The video track we receive from them, whatever its state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideo))]
    public partial IVideoFeed? Video { get; internal set; }

    /// <summary>Everyone's video hidden by us (local only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideo))]
    public partial bool VideoHidden { get; internal set; }

    [ObservableProperty]
    public partial bool Speaking { get; internal set; }

    /// <summary>0..1 from their audio receiver; drives the avatar's ring.</summary>
    [ObservableProperty]
    public partial double AudioLevel { get; internal set; }

    public string PlaceholderTitle => VideoHidden ? "You hid their video" : "Camera is off";

    partial void OnVideoHiddenChanged(bool value) => OnPropertyChanged(nameof(PlaceholderTitle));

    /// <summary>Letterboxed instead of cropped: while presenting, or toggled by a double-click.</summary>
    public bool Fit => _fit.Fit;

    public bool MicOff => !State.Audio;
    public bool IsPresenting => State.Screen;
    /// <summary>The avatar shows otherwise: no track yet, their camera is off, or we hid it.</summary>
    public bool ShowVideo => Video != null && State.Video && !VideoHidden;

    internal void UpdateState(MediaState state)
    {
        State = state;
        if (_fit.SetPresenting(state.Screen)) OnPropertyChanged(nameof(Fit));
    }

    /// <summary>The double-click: lasts until they start or stop presenting.</summary>
    public void ToggleFit()
    {
        _fit.Toggle();
        OnPropertyChanged(nameof(Fit));
    }
}

public static class GroupLabels
{
    /// <summary>"Name · 1a2b", as on the web: names are free text, so the id tells two Guests apart.</summary>
    public static string Label(string id, string? name) =>
        $"{(string.IsNullOrWhiteSpace(name) ? "Guest" : name.Trim())} · {ShortId(id)}";

    public static string ShortId(string id) => id.Length > 4 ? id[..4] : id;
}
