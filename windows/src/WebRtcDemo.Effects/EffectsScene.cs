using WebRtcDemo.Effects.Imaging;
using WebRtcDemo.Effects.Models;

namespace WebRtcDemo.Effects;

/// <summary>A looping, muted background video. Implementations decode on their own thread.</summary>
public interface IBackgroundVideo : IDisposable
{
    /// <summary>
    /// Copies the newest decoded frame into <paramref name="target"/> if it is newer than
    /// <paramref name="version"/>, and updates it. False when there is nothing new.
    /// </summary>
    bool TryCopyLatest(ref long version, BgraBitmap target);

    /// <summary>Playback failed after opening; the processor falls back to a blur.</summary>
    bool Failed { get; }
}

/// <summary>Opens a background video file; throws when it can't be played.</summary>
public delegate IBackgroundVideo BackgroundVideoFactory(string path);

/// <summary>
/// Everything the processor draws for one selection, loaded up front so switching is instant.
/// A background video is shared with the previous scene when the background did not change;
/// it stops when the last scene using it is disposed.
/// </summary>
public sealed class EffectsScene : IDisposable
{
    // Pictures are decoded no larger than the biggest camera frame the app captures.
    private const int MaxPictureSide = 1920;
    private const int MaxStickerSide = 512;

    private VideoLease? _video;

    private EffectsScene(EffectsSelection selection, BackgroundOption background, StickerOption? sticker)
    {
        Selection = selection;
        Background = background;
        Sticker = sticker;
    }

    public EffectsSelection Selection { get; }
    public BackgroundOption Background { get; }
    public StickerOption? Sticker { get; }
    public BgraBitmap? Picture { get; private init; }
    /// <summary>Premultiplied BGRA.</summary>
    public BgraBitmap? StickerImage { get; private init; }
    public IBackgroundVideo? Video => _video?.Video;

    public bool NeedsMask => Background.Kind != BackgroundKind.None;
    public bool NeedsFace => StickerImage != null;

    /// <summary>
    /// Decodes the files and loads the models the selection needs.
    /// </summary>
    /// <exception cref="Exception">Any file, model or video failure; nothing is kept.</exception>
    public static async Task<EffectsScene> LoadAsync(
        EffectsCatalog catalog, EffectsSelection selection, EffectsModels models,
        BackgroundVideoFactory? videos, EffectsScene? previous, CancellationToken cancellation = default)
    {
        var background = catalog.Background(selection.Background);
        var sticker = catalog.Sticker(selection.Sticker);
        var segmenter = background.Kind != BackgroundKind.None ? models.LoadSegmenterAsync() : null;
        var face = sticker != null ? models.LoadFaceAsync() : null;

        var picture = background is { Kind: BackgroundKind.Image, File: { } image }
            ? Task.Run(() => ImageFile.Load(image, MaxPictureSide, premultiply: false), cancellation)
            : null;
        var stickerImage = sticker != null
            ? Task.Run(() => ImageFile.Load(sticker.File, MaxStickerSide, premultiply: true), cancellation)
            : null;

        VideoLease? video = null;
        try
        {
            if (background is { Kind: BackgroundKind.Video, File: { } file })
            {
                // The same background keeps playing (no restart of the loop) across sticker changes.
                video = previous?.Background.Id == background.Id ? previous._video?.TryShare() : null;
                if (video == null)
                {
                    if (videos == null) throw new NotSupportedException("Background videos aren't supported here.");
                    video = new VideoLease(await Task.Run(() => videos(file), cancellation).ConfigureAwait(false));
                }
            }
            if (segmenter != null) await segmenter.ConfigureAwait(false);
            if (face != null) await face.ConfigureAwait(false);
            var scene = new EffectsScene(selection, background, sticker)
            {
                Picture = picture == null ? null : await picture.ConfigureAwait(false),
                StickerImage = stickerImage == null ? null : await stickerImage.ConfigureAwait(false),
            };
            cancellation.ThrowIfCancellationRequested();
            scene._video = video;
            return scene;
        }
        catch
        {
            video?.Release();
            throw;
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _video, null)?.Release();

    /// <summary>A video shared by consecutive scenes; the last one to let go stops it.</summary>
    private sealed class VideoLease(IBackgroundVideo video)
    {
        private int _references = 1;

        public IBackgroundVideo Video { get; } = video;

        public VideoLease? TryShare()
        {
            while (true)
            {
                var references = Volatile.Read(ref _references);
                if (references == 0 || Video.Failed) return null;
                if (Interlocked.CompareExchange(ref _references, references + 1, references) == references) return this;
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0) Video.Dispose();
        }
    }
}
