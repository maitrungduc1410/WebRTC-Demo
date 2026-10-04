using WebRtcDemo.Effects;
using WebRtcDemo.Effects.Imaging;

namespace WebRtcDemo.Core.Tests;

/// <summary>A port of android/.../StickerPlacementTest.kt: same faces, same expectations.</summary>
public class StickerPlacementTests
{
    private static StickerOption Sticker(StickerAnchor anchor = StickerAnchor.Eyes, float width = 2f, float? height = null, float offsetX = 0f, float offsetY = 0f) =>
        new("s", "S", "s.png", anchor, width, height, offsetX, offsetY);

    // Eyes 100 px apart at y = 100, nose and mouth below: an upright face.
    private static readonly FacePoints s_upright = new(
        EyeA: new Point(100f, 100f), EyeB: new Point(200f, 100f),
        Nose: new Point(150f, 150f), Mouth: new Point(150f, 200f));

    [Fact]
    public void Scales_by_the_distance_between_the_eyes()
    {
        var p = StickerPlacement.Place(s_upright, Sticker(width: 2f), aspect: 0.5f)!.Value;
        Assert.Equal(150f, p.X, 0.01f);
        Assert.Equal(100f, p.Y, 0.01f);
        Assert.Equal(200f, p.Width, 0.01f);
        Assert.Equal(100f, p.Height, 0.01f);
        Assert.Equal(0f, p.Angle, 0.0001f);
    }

    [Fact]
    public void Explicit_height_stretches_the_artwork() =>
        Assert.Equal(300f, StickerPlacement.Place(s_upright, Sticker(width: 2f, height: 3f), aspect: 0.5f)!.Value.Height, 0.01f);

    [Fact]
    public void Positive_offsetY_moves_up_the_face()
    {
        var p = StickerPlacement.Place(s_upright, Sticker(offsetY: 1f, offsetX: 0.5f), aspect: 1f)!.Value;
        Assert.Equal(200f, p.X, 0.01f);
        Assert.Equal(0f, p.Y, 0.01f);
    }

    [Fact]
    public void Anchors_on_nose_and_mouth()
    {
        Assert.Equal(150f, StickerPlacement.Place(s_upright, Sticker(StickerAnchor.Nose), 1f)!.Value.Y, 0.01f);
        Assert.Equal(200f, StickerPlacement.Place(s_upright, Sticker(StickerAnchor.Mouth), 1f)!.Value.Y, 0.01f);
    }

    [Fact]
    public void Eye_order_does_not_matter()
    {
        var swapped = s_upright with { EyeA = s_upright.EyeB, EyeB = s_upright.EyeA };
        Assert.Equal(StickerPlacement.Place(s_upright, Sticker(offsetX: 1f), 1f), StickerPlacement.Place(swapped, Sticker(offsetX: 1f), 1f));
    }

    [Fact]
    public void Follows_a_head_tilted_on_its_side()
    {
        // Rotated 90° clockwise on screen: the eyes stack vertically and the mouth is to the left.
        var tilted = new FacePoints(
            EyeA: new Point(100f, 100f), EyeB: new Point(100f, 200f),
            Nose: new Point(50f, 150f), Mouth: new Point(0f, 150f));
        var p = StickerPlacement.Place(tilted, Sticker(offsetY: 1f), aspect: 1f)!.Value;
        Assert.Equal(MathF.PI / 2, p.Angle, 0.0001f);
        // "Up" for this face points right on screen.
        Assert.Equal(200f, p.X, 0.01f);
        Assert.Equal(150f, p.Y, 0.01f);
    }

    [Fact]
    public void Keeps_its_size_when_the_head_turns()
    {
        // Turned sideways the eyes close up, but eyes to mouth stays 100 px.
        var turned = s_upright with { EyeA = new Point(130f, 100f), EyeB = new Point(170f, 100f) };
        Assert.Equal(100f / 1.2f, StickerPlacement.Place(turned, Sticker(width: 1f), aspect: 1f)!.Value.Width, 0.01f);
    }

    [Fact]
    public void Ignores_a_degenerate_face()
    {
        var point = new Point(10f, 10f);
        Assert.Null(StickerPlacement.Place(new FacePoints(point, point, point, point), Sticker(), 1f));
    }

    [Fact]
    public void Smoother_holds_through_short_gaps_then_lets_go()
    {
        var smoother = new PlacementSmoother(amount: 0.5f, keepFrames: 2);
        var a = new Placement(0f, 0f, 10f, 10f, 0f);
        Assert.Equal(a, smoother.Update(a));
        var b = smoother.Update(new Placement(10f, 0f, 10f, 10f, 0f));
        Assert.NotNull(b);
        Assert.Equal(5f, b.Value.X, 0.01f);
        Assert.NotNull(smoother.Update(null));
        Assert.NotNull(smoother.Update(null));
        Assert.Null(smoother.Update(null));
    }

    [Fact]
    public void Default_smoother_keeps_40_percent_and_six_misses()
    {
        var smoother = new PlacementSmoother();
        smoother.Update(new Placement(0f, 0f, 10f, 10f, 0f));
        Assert.Equal(6f, smoother.Update(new Placement(10f, 0f, 10f, 10f, 0f))!.Value.X, 0.01f);
        for (var i = 0; i < 6; i++) Assert.NotNull(smoother.Update(null));
        Assert.Null(smoother.Update(null));
    }

    [Fact]
    public void Smoother_turns_the_short_way_round()
    {
        var smoother = new PlacementSmoother(amount: 0.5f);
        smoother.Update(new Placement(0f, 0f, 1f, 1f, 3f));
        var angle = smoother.Update(new Placement(0f, 0f, 1f, 1f, -3f))!.Value.Angle;
        Assert.Equal(MathF.PI, MathF.Abs(angle), 0.01f);
    }
}

public sealed class EffectsCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("effects-catalog").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>The repository's effects folder, linked into the test output like the app's.</summary>
    internal static string RepoEffects => Path.Combine(AppContext.BaseDirectory, "effects");

    [Fact]
    public void Loads_the_shared_catalog_like_the_other_clients()
    {
        var catalog = EffectsCatalog.Load(RepoEffects);
        Assert.Equal(["none", "blur-light", "blur-strong"], catalog.Backgrounds.Take(3).Select(b => b.Id));
        Assert.Equal(0.008f, catalog.Background("blur-light").Blur);
        Assert.Equal(0.02f, catalog.Background("blur-strong").Blur);
        Assert.Equal(9, catalog.Backgrounds.Count(b => b.Kind == BackgroundKind.Image));
        Assert.Equal(3, catalog.Backgrounds.Count(b => b.Kind == BackgroundKind.Video));
        Assert.Equal(15, catalog.Stickers.Count);
        Assert.All(catalog.Backgrounds.Skip(3), b =>
        {
            Assert.True(File.Exists(b.File));
            Assert.True(File.Exists(b.Thumbnail));
        });
        Assert.All(catalog.Stickers, s => Assert.True(File.Exists(s.File)));
    }

    [Fact]
    public void Skips_missing_files_duplicates_reserved_ids_and_escapes()
    {
        Write("a.jpg");
        Write("s.png");
        Write("backgrounds.json", """
            {"backgrounds": [
              {"id": "a", "name": "A", "type": "image", "file": "a.jpg"},
              {"id": "a", "name": "Again", "type": "image", "file": "a.jpg"},
              {"id": "none", "name": "Reserved", "type": "image", "file": "a.jpg"},
              {"id": "gone", "name": "Gone", "type": "video", "file": "gone.mp4"},
              {"id": "up", "name": "Up", "type": "image", "file": "../a.jpg"},
              {"id": "odd", "name": "Odd", "type": "gif", "file": "a.jpg"},
              "junk"
            ]}
            """);
        Write("stickers.json", """
            {"stickers": [
              {"id": "s", "name": "S", "file": "s.png", "anchor": "nose", "width": 0, "height": 0, "offsetY": 0.5},
              {"id": "t", "name": "T", "file": "missing.png"}
            ]}
            """);
        var catalog = EffectsCatalog.Load(_root);
        var a = Assert.Single(catalog.Backgrounds.Skip(3));
        Assert.Equal(("a", "A", BackgroundKind.Image), (a.Id, a.Name, a.Kind));
        Assert.Equal(a.File, a.Thumbnail);
        var s = Assert.Single(catalog.Stickers);
        Assert.Equal((StickerAnchor.Nose, 1f, (float?)null, 0.5f), (s.Anchor, s.Width, s.Height, s.OffsetY));
    }

    [Fact]
    public void A_broken_manifest_still_offers_blur()
    {
        Write("backgrounds.json", "{ nope");
        var catalog = EffectsCatalog.Load(_root);
        Assert.Equal(3, catalog.Backgrounds.Count);
        Assert.Empty(catalog.Stickers);
    }

    [Fact]
    public void Sanitize_drops_what_is_no_longer_bundled()
    {
        var catalog = EffectsCatalog.Load(RepoEffects);
        Assert.Equal(EffectsSelection.Off, catalog.Sanitize(new EffectsSelection("deleted", "deleted")));
        var kept = new EffectsSelection("blur-strong", catalog.Stickers[0].Id);
        Assert.Equal(kept, catalog.Sanitize(kept));
        Assert.True(catalog.HasEffects(kept));
        Assert.False(catalog.HasEffects(EffectsSelection.Off));
    }
}

public class RasterTests
{
    private static BgraBitmap Solid(int w, int h, byte b, byte g, byte r)
    {
        var image = new BgraBitmap(w, h);
        for (var i = 0; i < w * h; i++)
        {
            image.Pixels[i * 4] = b;
            image.Pixels[i * 4 + 1] = g;
            image.Pixels[i * 4 + 2] = r;
            image.Pixels[i * 4 + 3] = 255;
        }
        return image;
    }

    [Fact]
    public void Area_resize_averages_blocks()
    {
        var src = new BgraBitmap(4, 2);
        for (var x = 0; x < 4; x++)
        for (var y = 0; y < 2; y++)
            src.Pixels[(y * 4 + x) * 4] = (byte)(x < 2 ? 0 : 200);
        var dst = new BgraBitmap(2, 1);
        Raster.ResizeArea(src, dst);
        Assert.Equal(0, dst.Pixels[0]);
        Assert.Equal(200, dst.Pixels[4]);
    }

    [Fact]
    public void Cover_crops_the_longer_side_symmetrically()
    {
        // A 4x2 picture, left half black and right half white, covering a 2x2 frame keeps the middle.
        var table = new ResampleTable().Configure(4, 2, 2, 2, cover: true);
        Assert.Equal([1, 2], table.X);
        Assert.Equal([0, 0], table.XWeight);
    }

    [Fact]
    public void Composite_uses_the_mask_with_a_smooth_edge()
    {
        var frame = Solid(4, 1, 0, 0, 255);
        var background = Solid(1, 1, 255, 0, 0);
        var mask = new byte[] { 0, 128, 200, 255 };
        var edge = Raster.EdgeTable(0.3f, 0.7f);
        Raster.Composite(frame, background, new ResampleTable().Configure(1, 1, 4, 1, false),
            mask, 4, 1, new ResampleTable().Configure(4, 1, 4, 1, false), edge);
        Assert.Equal((255, 0), (frame.Pixels[0], frame.Pixels[2]));
        Assert.InRange(frame.Pixels[6], 100, 155);
        Assert.Equal((0, 255), (frame.Pixels[12], frame.Pixels[14]));
        Assert.Equal(0, edge[(int)(0.3f * 255)]);
        Assert.Equal(255, edge[(int)MathF.Ceiling(0.7f * 255)]);
    }

    [Fact]
    public void Blur_keeps_flat_color_and_spreads_an_edge()
    {
        var image = Solid(32, 8, 10, 20, 30);
        Raster.GaussianBlur(image, new BlurKernel(2.5f), new BgraBitmap(2, 2));
        Assert.All(Enumerable.Range(0, 32 * 8), i => Assert.Equal((10, 20, 30), (image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2])));

        var edge = new BgraBitmap(32, 1);
        for (var x = 16; x < 32; x++) edge.Pixels[x * 4] = 255;
        Raster.GaussianBlur(edge, new BlurKernel(2.5f), new BgraBitmap(2, 2));
        Assert.InRange(edge.Pixels[15 * 4], 60, 127);
        Assert.InRange(edge.Pixels[16 * 4], 128, 195);
        Assert.Equal(0, edge.Pixels[0]);
        Assert.Equal(255, edge.Pixels[31 * 4]);
    }

    [Fact]
    public void Sticker_is_drawn_rotated_with_premultiplied_alpha()
    {
        var frame = Solid(40, 40, 0, 0, 0);
        // 20x10, opaque white left half, 50% transparent white right half (premultiplied).
        var sticker = new BgraBitmap(20, 10);
        for (var y = 0; y < 10; y++)
        for (var x = 0; x < 20; x++)
        {
            var v = (byte)(x < 10 ? 255 : 128);
            sticker.Pixels.AsSpan((y * 20 + x) * 4, 4).Fill(v);
        }
        Raster.DrawSticker(frame, sticker, new Placement(20, 20, 20, 10, MathF.PI / 2));
        byte At(int x, int y) => frame.Pixels[(y * 40 + x) * 4];
        // Turned clockwise a quarter: the sticker's left half is now on top.
        Assert.Equal(255, At(20, 13));
        Assert.InRange(At(20, 27), 120, 136);
        Assert.Equal(0, At(10, 20));
        Assert.Equal(0, At(20, 5));
    }

    [Fact]
    public void I420_matches_bt601_limited_range()
    {
        var white = Solid(4, 2, 255, 255, 255);
        var red = Solid(4, 2, 0, 0, 255);
        var i420 = new I420Buffer();
        Raster.BgraToI420(white, i420);
        Assert.Equal((235, 128, 128), (i420.Y[0], i420.U[0], i420.V[0]));
        Raster.BgraToI420(red, i420);
        Assert.Equal((82, 90, 240), (i420.Y[0], i420.U[0], i420.V[0]));
        var odd = new I420Buffer();
        Raster.BgraToI420(Solid(3, 3, 0, 0, 0), odd);
        Assert.Equal((2, 4, 16), (odd.StrideUV, odd.U.Length, odd.Y[8]));
    }
}
