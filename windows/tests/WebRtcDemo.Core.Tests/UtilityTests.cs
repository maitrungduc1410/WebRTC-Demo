using Microsoft.Extensions.Time.Testing;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Core.Settings;
using WebRtcDemo.Core.Tests.Fakes;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Tests;

public class ServerAddressTests
{
    // Expected values come from the web client's normalizeServerUrl run in Node (WHATWG URL).
    [Theory]
    [InlineData("192.168.1.10:4000", "http://192.168.1.10:4000")]
    [InlineData("  http://192.168.1.10:4000/  ", "http://192.168.1.10:4000")]
    [InlineData("https://Example.COM/path?q=1#x", "https://example.com")]
    [InlineData("http://user:pw@host.local:8080/ns", "http://host.local:8080")]
    [InlineData("HTTP://LOCALHOST:4000", "http://localhost:4000")]
    [InlineData("https://example.com:443", "https://example.com")]
    [InlineData("http://example.com:80", "http://example.com")]
    [InlineData("localhost", "http://localhost")]
    [InlineData("localhost:4000", "http://localhost:4000")]
    [InlineData("[::1]:4000", "http://[::1]:4000")]
    [InlineData("http://[::1]", "http://[::1]")]
    [InlineData("10.0.0.2", "http://10.0.0.2")]
    [InlineData("https://münchen.de:8443", "https://xn--mnchen-3ya.de:8443")]
    [InlineData("host:4000/socket.io", "http://host:4000")]
    [InlineData("mailto:x@y.z", "http://y.z")]
    [InlineData("ftp://example.com", null)]
    [InlineData("ws://example.com", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("http://", null)]
    [InlineData("example.com:99999", null)]
    [InlineData("http://exa mple.com", null)]
    public void Normalize_matches_the_web_client(string input, string? expected) =>
        Assert.Equal(expected, ServerAddress.Normalize(input));
}

public class AvatarTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("hello", 99162322)]
    [InlineData("you", 119839)]
    [InlineData("peer-123456", -381940434)]
    [InlineData("peer-🙂", -688770576)]
    [InlineData("polygenelubricants", int.MinValue)]
    public void Hash_is_Java_String_hashCode(string seed, int expected) => Assert.Equal(expected, Avatar.JavaHashCode(seed));

    [Theory]
    [InlineData("you", 1)]
    [InlineData("peer-123456", 0)]
    [InlineData("peer-000000", 3)]
    [InlineData("hello", 4)]
    [InlineData("peer-room-42", 5)]
    // Int32.MinValue: Swift's magnitude is 2147483648, and 2147483648 % 6 == 2.
    [InlineData("polygenelubricants", 2)]
    public void Palette_matches_iOS(string seed, int palette) => Assert.Equal(Avatar.Palettes[palette], Avatar.Colors(seed));

    [Fact]
    public void Remote_seed_matches_iOS() => Assert.Equal("peer-123456", Avatar.RemoteSeed("123456"));
}

public class VideoLayoutTests
{
    [Theory]
    [InlineData(false, 1280, 720, 1600, 900, VideoFit.Fill)]
    [InlineData(false, 720, 1280, 1600, 900, VideoFit.Fit)]
    [InlineData(false, 720, 1280, 400, 800, VideoFit.Fill)]
    [InlineData(true, 1920, 1080, 1600, 900, VideoFit.Fit)]
    [InlineData(false, 0, 0, 1600, 900, VideoFit.Fill)]
    public void Default_fit(bool screen, int fw, int fh, double vw, double vh, VideoFit expected) =>
        Assert.Equal(expected, VideoLayout.DefaultFit(screen, fw, fh, vw, vh));

    [Theory]
    [InlineData(1280, 720, 1600, 900, 1.25)]
    [InlineData(720, 1280, 1600, 900, 1600.0 / 720)]
    [InlineData(1920, 1080, 400, 800, 800.0 / 1080)]
    [InlineData(640, 480, 640, 480, 1.0)]
    public void Cover_scale_fills_both_directions(int fw, int fh, double vw, double vh, double expected)
    {
        var scale = VideoLayout.CoverScale(fw, fh, vw, vh)!.Value;
        Assert.Equal(expected, scale, 9);
        Assert.True(fw * scale >= vw - 1e-9 && fh * scale >= vh - 1e-9);
        Assert.True(Math.Abs(fw * scale - vw) < 1e-9 || Math.Abs(fh * scale - vh) < 1e-9);
    }

    [Theory]
    [InlineData(0, 720, 1600, 900)]
    [InlineData(1280, 720, 0, 900)]
    public void Cover_scale_is_unknown_without_sizes(int fw, int fh, double vw, double vh) =>
        Assert.Null(VideoLayout.CoverScale(fw, fh, vw, vh));

    [Theory]
    [InlineData(1280, 720, 1600, 900)]
    [InlineData(720, 1280, 1600, 900)]
    [InlineData(1920, 1080, 400, 800)]
    public void Fit_scale_shrinks_the_cover_to_the_letterboxed_frame(int fw, int fh, double vw, double vh)
    {
        Assert.Equal(1, VideoLayout.FitScale(VideoFit.Fill, fw, fh, vw, vh));
        var shown = VideoLayout.CoverScale(fw, fh, vw, vh)!.Value * VideoLayout.FitScale(VideoFit.Fit, fw, fh, vw, vh);
        Assert.Equal(Math.Min(vw / fw, vh / fh), shown, 9);
        Assert.True(fw * shown <= vw + 1e-9 && fh * shown <= vh + 1e-9);
    }

    [Fact]
    public void Fit_scale_is_one_while_sizes_are_unknown_or_aspects_match()
    {
        Assert.Equal(1, VideoLayout.FitScale(VideoFit.Fit, 0, 0, 1600, 900));
        Assert.Equal(1, VideoLayout.FitScale(VideoFit.Fit, 1280, 720, 1600, 900), 9);
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "webrtcdemo-tests-" + Guid.NewGuid().ToString("N"));

    private SettingsStore Store => new(Path.Combine(_dir, "settings.json"));

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var settings = Store.Load();
        Assert.Null(settings.ServerUrl);
        Assert.Equal(ServerAddress.DefaultUrl, settings.EffectiveServerUrl);
    }

    [Fact]
    public void Round_trips_and_stores_the_default_server_as_nothing()
    {
        Store.Save(new AppSettings { ServerUrl = "10.0.0.5:4000", E2ee = true, CameraId = "cam" });
        var loaded = Store.Load();
        Assert.Equal("http://10.0.0.5:4000", loaded.ServerUrl);
        Assert.True(loaded.E2ee);
        Assert.Equal("cam", loaded.CameraId);

        Store.Update(s => s with { ServerUrl = ServerAddress.DefaultUrl });
        Assert.Null(Store.Load().ServerUrl);
        Assert.DoesNotContain("serverUrl", File.ReadAllText(Store.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_file_gives_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Store.FilePath, "{ not json");
        Assert.Equal(new AppSettings(), Store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

public class ImageOpsTests
{
    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    [Fact]
    public void Downscale_keeps_the_aspect_ratio_and_color()
    {
        var image = ImageOps.Downscale(Solid(1280, 720, 10, 20, 200), 1280, 720, 1280 * 4, 36);
        Assert.Equal(36, image.Width);
        Assert.Equal(20, image.Height);
        Assert.Equal(new byte[] { 10, 20, 200, 255 }, image.Pixels[..4]);
    }

    [Fact]
    public void Downscale_honors_the_stride()
    {
        // 4x2 image in an 8-pixel-wide buffer whose padding is white.
        var buffer = Solid(8, 2, 255, 255, 255);
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 4; x++) Array.Copy(new byte[] { 0, 0, 0, 255 }, 0, buffer, y * 32 + x * 4, 4);
        }
        var image = ImageOps.Downscale(buffer, 4, 2, 32, 2);
        Assert.All(image.Pixels.Chunk(4), p => Assert.Equal(0, p[0]));
    }

    [Fact]
    public void Luma_and_blur()
    {
        var black = new BgraImage(Solid(36, 20, 0, 0, 0), 36, 20);
        Assert.True(ImageOps.MeanLuma(black) < RemoteSnapshotter.MinimumLuma);
        var gray = new BgraImage(Solid(36, 20, 128, 128, 128), 36, 20);
        Assert.Equal(128, ImageOps.MeanLuma(gray), 3);
        Assert.Equal(gray.Pixels, ImageOps.Blur(gray, 2).Pixels);

        // A single white pixel spreads out.
        var dot = new BgraImage(Solid(9, 9, 0, 0, 0), 9, 9);
        dot.Pixels[(4 * 9 + 4) * 4] = 255;
        var blurred = ImageOps.Blur(dot, 1);
        Assert.InRange(blurred.Pixels[(4 * 9 + 4) * 4], 1, 254);
        Assert.True(blurred.Pixels[(4 * 9 + 5) * 4] > 0);
    }
}

public class RemoteSnapshotterTests
{
    private static void Deliver(RemoteSnapshotter snapshotter, byte value)
    {
        var pixels = new byte[64 * 48 * 4];
        Array.Fill(pixels, value);
        snapshotter.OnFrame(new VideoFrame(pixels, 64, 48, 64 * 4, 0));
    }

    [Fact]
    public void Takes_one_snapshot_per_interval_and_skips_black_frames()
    {
        using var dispatcher = new ManualDispatcher();
        var time = new FakeTimeProvider();
        var snapshots = new List<BgraImage>();
        using var snapshotter = new RemoteSnapshotter(dispatcher, snapshots.Add, time);

        Deliver(snapshotter, 200);
        Deliver(snapshotter, 200);
        dispatcher.RunPending();
        Assert.Single(snapshots);
        Assert.Equal(RemoteSnapshotter.SnapshotWidth, snapshots[0].Width);

        time.Advance(TimeSpan.FromMilliseconds(510));
        Deliver(snapshotter, 3);
        dispatcher.RunPending();
        Assert.Single(snapshots);

        time.Advance(TimeSpan.FromMilliseconds(510));
        snapshotter.Active = false;
        Deliver(snapshotter, 200);
        dispatcher.RunPending();
        Assert.Single(snapshots);
    }
}

public class AudioDeviceSelectionTests
{
    private readonly List<int> _selected = [];
    private readonly AudioDeviceSelection _mics;

    public AudioDeviceSelectionTests() => _mics = new AudioDeviceSelection(index =>
    {
        _selected.Add(index);
        return true;
    });

    private static DeviceInfo[] List(params string[] ids) => [.. ids.Select((id, i) => new DeviceInfo(i, "Mic " + id, id))];

    [Fact]
    public void The_first_list_shows_the_default_device_and_selects_nothing()
    {
        Assert.False(_mics.Update(List("usb", "headset", "array"), defaultId: "array"));
        Assert.Equal("array", _mics.Id);
        Assert.False(_mics.Pinned);
        Assert.Empty(_selected);
    }

    [Fact]
    public void Without_a_known_default_the_first_device_is_shown()
    {
        _mics.Update(List("usb", "array"), defaultId: null);
        Assert.Equal("usb", _mics.Id);
        _mics.Update(List("usb", "array"), defaultId: "gone");
        Assert.Equal("usb", _mics.Id);
        Assert.Empty(_selected);
    }

    [Fact]
    public void A_picked_device_that_moves_is_selected_again_under_its_new_index()
    {
        _mics.Update(List("usb", "headset", "array"), "array");
        Assert.True(_mics.Select("headset"));
        Assert.Equal([1], _selected);

        Assert.False(_mics.Update(List("headset", "array"), "array"));
        Assert.Equal("headset", _mics.Id);
        Assert.Equal([1, 0], _selected);

        // Same place: nothing to tell the module.
        Assert.False(_mics.Update(List("headset", "array", "usb"), "array"));
        Assert.Equal([1, 0], _selected);
    }

    [Fact]
    public void A_device_followed_as_the_default_is_not_pinned_when_others_move()
    {
        _mics.Update(List("usb", "array"), "array");
        Assert.False(_mics.Update(List("array"), "array"));
        Assert.Empty(_selected);
    }

    [Fact]
    public void Removing_the_device_in_use_switches_once_to_the_default()
    {
        _mics.Update(List("array", "usb", "headset"), "array");
        _mics.Select("headset");
        _selected.Clear();

        Assert.True(_mics.Update(List("array", "usb"), "array"));
        Assert.Equal("array", _mics.Id);
        Assert.Equal([0], _selected);
        Assert.Equal(new MediaDevice("array", "Mic array"), _mics.Current);

        Assert.False(_mics.Update(List("array", "usb"), "array"));
        Assert.Equal([0], _selected);
    }

    [Fact]
    public void A_picked_device_that_goes_away_leaves_the_selection_following_the_default()
    {
        _mics.Update(List("array", "usb"), "array");
        _mics.Select("usb");
        _selected.Clear();
        Assert.True(_mics.Update(List("array"), "array"));
        Assert.False(_mics.Pinned);

        Assert.True(_mics.Update(List("array", "headset"), "headset"));
        Assert.Equal("headset", _mics.Id);
        Assert.Equal([0, 1], _selected);
    }

    [Fact]
    public void While_unpicked_a_new_default_is_followed_and_selected()
    {
        _mics.Update(List("usb", "headset"), "usb");
        Assert.Empty(_selected);

        // A headset made the default, between calls or mid-call: the module would stay on usb.
        Assert.True(_mics.Update(List("usb", "headset"), "headset"));
        Assert.Equal("headset", _mics.Id);
        Assert.False(_mics.Pinned);
        Assert.Equal([1], _selected);

        Assert.False(_mics.Update(List("usb", "headset"), "headset"));
        Assert.Equal([1], _selected);
    }

    [Fact]
    public void While_unpicked_an_unplugged_default_switches_to_the_new_default()
    {
        _mics.Update(List("usb", "headset", "array"), "headset");

        // Windows names a new default when the old one goes; the module does not move by itself.
        Assert.True(_mics.Update(List("usb", "array"), "array"));
        Assert.Equal("array", _mics.Id);
        Assert.Equal(new MediaDevice("array", "Mic array"), _mics.Current);
        Assert.Equal([1], _selected);

        // Now on an index: another device going shifts it.
        Assert.False(_mics.Update(List("array"), "array"));
        Assert.Equal([1, 0], _selected);
    }

    [Fact]
    public void While_unpicked_without_a_known_default_the_device_in_use_stays()
    {
        _mics.Update(List("usb", "array"), null);
        Assert.False(_mics.Update(List("array", "usb"), null));
        Assert.Equal("usb", _mics.Id);
        Assert.Empty(_selected);

        Assert.True(_mics.Update(List("array"), null));
        Assert.Equal("array", _mics.Id);
        Assert.Equal([0], _selected);
    }

    [Fact]
    public void After_all_went_the_first_to_come_back_is_selected()
    {
        _mics.Update(List("usb"), "usb");
        Assert.True(_mics.Update([], null));
        Assert.Null(_mics.Id);
        Assert.Null(_mics.Current);
        Assert.False(_mics.Update([], null));

        Assert.True(_mics.Update(List("headset"), "headset"));
        Assert.Equal("headset", _mics.Id);
        Assert.Equal([0], _selected);
    }

    [Fact]
    public void A_device_the_module_refuses_is_never_reported_as_the_new_one()
    {
        var accept = true;
        var mics = new AudioDeviceSelection(_ => accept);
        mics.Update(List("usb", "headset"), "usb");
        accept = false;

        // A new default it can't switch to: stay on the one in use, no change to report.
        Assert.False(mics.Update(List("usb", "headset"), "headset"));
        Assert.Equal("usb", mics.Id);

        // The one in use went and the default can't be taken: reported as disconnected, once.
        Assert.True(mics.Update(List("headset"), "headset"));
        Assert.Null(mics.Id);
        Assert.Null(mics.Current);
        Assert.False(mics.Update(List("headset", "array"), "headset"));
        Assert.Null(mics.Id);

        accept = true;
        Assert.True(mics.Update(List("headset", "array"), "array"));
        Assert.Equal("array", mics.Id);
    }

    [Fact]
    public void Unchanged_lists_are_recognized()
    {
        Assert.False(_mics.IsUnchanged([], null));
        _mics.Update(List("usb", "array"), "array");
        Assert.True(_mics.IsUnchanged(List("usb", "array"), "array"));
        Assert.False(_mics.IsUnchanged(List("usb", "array"), "usb"));
        Assert.False(_mics.IsUnchanged(List("array", "usb"), "array"));
    }
}

public sealed class MicLevelTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-0.5, 0)]
    [InlineData(0.001, 0)] // -60 dBFS: below the scale
    [InlineData(0.00316227766, 0)] // -50 dBFS
    [InlineData(0.1, 0.75)] // -20 dBFS
    [InlineData(0.316227766, 1)] // -10 dBFS
    [InlineData(1, 1)]
    public void Peak_maps_minus_50_to_minus_10_dBFS_onto_0_to_1(double peak, double level) =>
        Assert.Equal(level, MicLevel.FromPeak(peak), 6);

    [Fact]
    public void Level_decays_by_a_quarter_per_poll_and_snaps_to_silence()
    {
        Assert.Equal(0.75, MicLevel.Next(1, 0));
        Assert.Equal(0.75, MicLevel.Next(1, null));
        Assert.Equal(0.5625, MicLevel.Next(0.75, 0.001));
        Assert.Equal(0.75, MicLevel.Next(0.2, 0.1), 6);
        Assert.Equal(0, MicLevel.Next(0.012, 0));
    }
}

public sealed class IdleMicMeterTests
{
    private readonly List<string?> _started = [];
    private readonly List<FakePeakMeter> _meters = [];

    private IdleMicMeter Create(double? peak = 0.5) => new(id =>
    {
        _started.Add(id);
        var meter = new FakePeakMeter(peak);
        _meters.Add(meter);
        return meter;
    });

    [Fact]
    public void First_read_opens_the_device_and_later_reads_measure()
    {
        var idle = Create();
        Assert.Null(idle.Read("mic-a"));
        Assert.True(idle.Running);
        Assert.Equal(0.5, idle.Read("mic-a"));
        Assert.Equal(["mic-a"], _started);
    }

    [Fact]
    public void A_device_change_releases_the_old_meter_and_opens_the_new_one()
    {
        var idle = Create();
        idle.Read("mic-a");
        Assert.Null(idle.Read("mic-b"));
        Assert.True(_meters[0].Disposed);
        Assert.False(_meters[1].Disposed);
        Assert.Equal(["mic-a", "mic-b"], _started);

        idle.Read(null);
        Assert.True(_meters[1].Disposed);
        Assert.Equal(["mic-a", "mic-b", null], _started);
    }

    [Fact]
    public void Stop_releases_the_device_and_the_next_read_reopens_it()
    {
        var idle = Create();
        idle.Read("mic-a");
        idle.Stop();
        Assert.True(_meters[0].Disposed);
        Assert.False(idle.Running);
        idle.Stop();

        Assert.Null(idle.Read("mic-a"));
        Assert.Equal(2, _started.Count);
    }

    [Fact]
    public void An_unavailable_meter_is_not_retried_every_read()
    {
        var starts = 0;
        var idle = new IdleMicMeter(_ =>
        {
            starts++;
            return null;
        });
        Assert.Null(idle.Read("mic-a"));
        Assert.Null(idle.Read("mic-a"));
        Assert.False(idle.Running);
        Assert.Equal(1, starts);
    }
}
