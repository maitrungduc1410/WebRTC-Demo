using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Media;

/// <summary>
/// The microphones (or speakers) and the one the audio device module uses. Until one is picked,
/// <see cref="Id"/> follows the Windows default communications device. libwebrtc starts on that
/// device by role, but neither moves to a new default nor recovers from losing it while running,
/// so every later change of the device in use is selected explicitly. The module selects by list
/// index; once it has been given one, a device that moves in the list is selected again under its
/// new index.
/// </summary>
/// <param name="select">Tells the module to use the device at a list index.</param>
internal sealed class AudioDeviceSelection(Func<int, bool> select)
{
    private bool _listed;
    private bool _indexed;
    private string? _defaultId;

    public IReadOnlyList<DeviceInfo> Infos { get; private set; } = [];
    public IReadOnlyList<MediaDevice> Devices { get; private set; } = [];
    public string? Id { get; private set; }
    /// <summary>Picked rather than following the default device (until it goes away).</summary>
    public bool Pinned { get; private set; }
    public MediaDevice? Current => Devices.FirstOrDefault(d => d.Id == Id);

    public bool IsUnchanged(IReadOnlyList<DeviceInfo> infos, string? defaultId) =>
        _listed && defaultId == _defaultId && infos.SequenceEqual(Infos);

    /// <summary>
    /// Takes a new list and default. A picked device stays while it is listed; otherwise the
    /// default one is used, else the one in use while it is listed, else the first.
    /// </summary>
    /// <param name="defaultId">The default communications device; null where the module can't tell.</param>
    /// <returns>Whether the device in use changed (the first list only shows the one libwebrtc starts on).</returns>
    public bool Update(IReadOnlyList<DeviceInfo> infos, string? defaultId)
    {
        var first = !_listed;
        var previousIndex = IndexOf(Infos, Id);
        _listed = true;
        _defaultId = defaultId;
        Infos = infos;
        Devices = [.. infos.Select(d => new MediaDevice(d.Id, d.Name))];

        if (Pinned && IndexOf(infos, Id) < 0) Pinned = false;
        var target = Pinned ? Id
            : IndexOf(infos, defaultId) >= 0 ? defaultId
            : IndexOf(infos, Id) >= 0 ? Id
            : Devices.Count > 0 ? Devices[0].Id : null;
        if (first)
        {
            Id = target;
            return false;
        }
        if (target == Id)
        {
            var index = IndexOf(infos, Id);
            if (_indexed && index >= 0 && index != previousIndex) select(index);
            return false;
        }
        if (target != null && !select(IndexOf(infos, target)))
        {
            // The module didn't take it: report the one in use as gone (if it is), never the new one.
            if (Id == null || IndexOf(infos, Id) >= 0) return false;
            Id = null;
            return true;
        }
        Id = target;
        if (target != null) _indexed = true;
        return true;
    }

    public bool Select(string id)
    {
        var index = IndexOf(Infos, id);
        if (index < 0 || !select(index)) return false;
        Id = id;
        Pinned = true;
        _indexed = true;
        return true;
    }

    public int IndexOf(string? id) => IndexOf(Infos, id);

    internal static int IndexOf(IReadOnlyList<DeviceInfo> devices, string? id) =>
        id == null ? -1 : devices.FirstOrDefault(d => d.Id == id)?.Index ?? -1;
}
