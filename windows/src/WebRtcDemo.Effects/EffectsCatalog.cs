using System.Text.Json;

namespace WebRtcDemo.Effects;

public enum BackgroundKind { None, Blur, Image, Video }

/// <param name="Blur">Blur radius as a fraction of the frame width.</param>
/// <param name="File">Absolute path of the picture or video.</param>
public sealed record BackgroundOption(string Id, string Name, BackgroundKind Kind, float Blur = 0, string? File = null, string? Thumbnail = null);

public enum StickerAnchor { Eyes, Nose, Mouth }

/// <summary>Sizes and offsets are in units of the distance between the eyes; positive OffsetY is up.</summary>
/// <param name="Height">Stretches the artwork; null keeps the picture's own aspect ratio.</param>
public sealed record StickerOption(string Id, string Name, string File, StickerAnchor Anchor, float Width, float? Height = null, float OffsetX = 0, float OffsetY = 0);

public sealed record EffectsSelection(string Background = EffectsSelection.None, string? Sticker = null)
{
    public const string None = "none";
    public static readonly EffectsSelection Off = new();
}

/// <summary>
/// The backgrounds and stickers of the repository's effects folder (shared with the web, Android
/// and iOS apps, linked into the app output). Entries whose file is missing are skipped.
/// </summary>
public sealed class EffectsCatalog
{
    public static readonly IReadOnlyList<BackgroundOption> BuiltIn =
    [
        new(EffectsSelection.None, "None", BackgroundKind.None),
        new("blur-light", "Slight blur", BackgroundKind.Blur, Blur: 0.008f),
        new("blur-strong", "Blur", BackgroundKind.Blur, Blur: 0.02f),
    ];

    public static readonly EffectsCatalog Empty = new(BuiltIn, []);

    public EffectsCatalog(IReadOnlyList<BackgroundOption> backgrounds, IReadOnlyList<StickerOption> stickers)
    {
        Backgrounds = backgrounds;
        Stickers = stickers;
    }

    /// <summary>Starts with <see cref="BuiltIn"/>.</summary>
    public IReadOnlyList<BackgroundOption> Backgrounds { get; }
    public IReadOnlyList<StickerOption> Stickers { get; }

    public BackgroundOption Background(string? id) => Backgrounds.FirstOrDefault(b => b.Id == id) ?? Backgrounds[0];

    public StickerOption? Sticker(string? id) => id == null ? null : Stickers.FirstOrDefault(s => s.Id == id);

    public bool HasEffects(EffectsSelection selection) =>
        Background(selection.Background).Kind != BackgroundKind.None || Sticker(selection.Sticker) != null;

    /// <summary>Drops choices that are no longer bundled.</summary>
    public EffectsSelection Sanitize(EffectsSelection selection) =>
        new(Background(selection.Background).Id, Sticker(selection.Sticker)?.Id);

    /// <summary>Reads backgrounds.json and stickers.json under <paramref name="root"/>; a missing or bad file lists nothing.</summary>
    public static EffectsCatalog Load(string root) =>
        new([.. BuiltIn, .. ReadBackgrounds(root)], ReadStickers(root));

    private static List<BackgroundOption> ReadBackgrounds(string root)
    {
        var ids = BuiltIn.Select(b => b.Id).ToHashSet();
        var result = new List<BackgroundOption>();
        foreach (var entry in Entries(root, "backgrounds.json", "backgrounds"))
        {
            var id = String(entry, "id");
            var kind = String(entry, "type") switch
            {
                "image" => BackgroundKind.Image,
                "video" => BackgroundKind.Video,
                _ => (BackgroundKind?)null,
            };
            var file = Existing(root, String(entry, "file"));
            if (string.IsNullOrEmpty(id) || kind == null || file == null || !ids.Add(id)) continue;
            var thumbnail = Existing(root, String(entry, "thumbnail")) ?? (kind == BackgroundKind.Image ? file : null);
            result.Add(new BackgroundOption(id, NonEmpty(String(entry, "name")) ?? id, kind.Value, File: file, Thumbnail: thumbnail));
        }
        return result;
    }

    private static List<StickerOption> ReadStickers(string root)
    {
        var result = new List<StickerOption>();
        foreach (var entry in Entries(root, "stickers.json", "stickers"))
        {
            var id = String(entry, "id");
            var file = Existing(root, String(entry, "file"));
            if (string.IsNullOrEmpty(id) || file == null) continue;
            var anchor = String(entry, "anchor") switch
            {
                "nose" => StickerAnchor.Nose,
                "mouth" => StickerAnchor.Mouth,
                _ => StickerAnchor.Eyes,
            };
            result.Add(new StickerOption(id, NonEmpty(String(entry, "name")) ?? id, file, anchor,
                Number(entry, "width") is { } width and not 0 ? width : 1,
                Number(entry, "height") is { } height and not 0 ? height : null,
                Number(entry, "offsetX") ?? 0,
                Number(entry, "offsetY") ?? 0));
        }
        return result;
    }

    private static List<JsonElement> Entries(string root, string manifest, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(root, manifest)));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            return [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone())];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static string? String(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float? Number(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetSingle() : null;

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>The absolute path of a manifest-relative file, if it exists inside the folder.</summary>
    private static string? Existing(string root, string? relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var inside = full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return inside && System.IO.File.Exists(full) ? full : null;
    }
}
