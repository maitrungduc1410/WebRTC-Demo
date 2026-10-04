using StbImageSharp;

namespace WebRtcDemo.Effects.Imaging;

/// <summary>A BGRA image, tightly packed (stride = width * 4). Reused across frames by resizing in place.</summary>
public sealed class BgraBitmap
{
    public BgraBitmap(int width, int height)
    {
        Resize(width, height);
    }

    public BgraBitmap(byte[] pixels, int width, int height)
    {
        if (pixels.Length < width * height * 4) throw new ArgumentException("Too few pixels.", nameof(pixels));
        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public byte[] Pixels { get; private set; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * 4;

    /// <summary>Keeps the buffer when it is large enough, so steady-state frames do not allocate.</summary>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var size = width * height * 4;
        if (Pixels.Length < size) Pixels = new byte[size];
        Width = width;
        Height = height;
    }

    public Span<byte> Span => Pixels.AsSpan(0, Width * Height * 4);

    public void CopyFrom(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        Resize(width, height);
        var row = width * 4;
        if (stride == row)
        {
            bgra[..(row * height)].CopyTo(Pixels);
            return;
        }
        for (var y = 0; y < height; y++) bgra.Slice(y * stride, row).CopyTo(Pixels.AsSpan(y * row, row));
    }
}

/// <summary>Planar I420 (BT.601 limited range), the format the custom WebRTC source takes.</summary>
public sealed class I420Buffer
{
    public byte[] Y { get; private set; } = [];
    public byte[] U { get; private set; } = [];
    public byte[] V { get; private set; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int StrideY => Width;
    public int StrideUV => (Width + 1) / 2;

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        var chroma = StrideUV * ((height + 1) / 2);
        if (Y.Length < width * height) Y = new byte[width * height];
        if (U.Length < chroma)
        {
            U = new byte[chroma];
            V = new byte[chroma];
        }
    }
}

public static class ImageFile
{
    /// <summary>
    /// Decodes a JPEG or PNG into BGRA, box-filtered so the longer side is at most
    /// <paramref name="maxSide"/>. Premultiplied alpha when <paramref name="premultiply"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a picture stb_image can read.</exception>
    public static BgraBitmap Load(string path, int maxSide, bool premultiply)
    {
        ImageResult image;
        try
        {
            using var stream = File.OpenRead(path);
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e) when (e is not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Couldn't decode {Path.GetFileName(path)}: {e.Message}", e);
        }
        var bgra = new BgraBitmap(image.Width, image.Height);
        var src = image.Data;
        var dst = bgra.Pixels;
        for (var i = 0; i < image.Width * image.Height * 4; i += 4)
        {
            var a = src[i + 3];
            if (premultiply && a != 255)
            {
                dst[i] = (byte)((src[i + 2] * a + 127) / 255);
                dst[i + 1] = (byte)((src[i + 1] * a + 127) / 255);
                dst[i + 2] = (byte)((src[i] * a + 127) / 255);
            }
            else
            {
                dst[i] = src[i + 2];
                dst[i + 1] = src[i + 1];
                dst[i + 2] = src[i];
            }
            dst[i + 3] = a;
        }
        var longSide = Math.Max(image.Width, image.Height);
        if (longSide <= maxSide) return bgra;
        var scale = (double)maxSide / longSide;
        var small = new BgraBitmap(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
        Raster.ResizeArea(bgra, small);
        return small;
    }
}
