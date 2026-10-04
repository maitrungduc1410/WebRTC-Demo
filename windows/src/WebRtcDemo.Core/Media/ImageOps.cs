namespace WebRtcDemo.Core.Media;

/// <summary>A small BGRA image, tightly packed (stride = width * 4).</summary>
public sealed record BgraImage(byte[] Pixels, int Width, int Height);

public static class ImageOps
{
    /// <summary>Box-filters a BGRA frame down to <paramref name="targetWidth"/>, keeping the aspect ratio.</summary>
    public static BgraImage Downscale(ReadOnlySpan<byte> bgra, int width, int height, int stride, int targetWidth)
    {
        targetWidth = Math.Clamp(targetWidth, 1, width);
        var targetHeight = Math.Max(1, (int)Math.Round((double)targetWidth * height / width));
        var output = new byte[targetWidth * targetHeight * 4];
        for (var ty = 0; ty < targetHeight; ty++)
        {
            var y0 = ty * height / targetHeight;
            var y1 = Math.Max(y0 + 1, (ty + 1) * height / targetHeight);
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var x0 = tx * width / targetWidth;
                var x1 = Math.Max(x0 + 1, (tx + 1) * width / targetWidth);
                // Sampling a 4x4 grid of the source block is plenty for a 36 px snapshot.
                int b = 0, g = 0, r = 0, n = 0;
                var stepY = Math.Max(1, (y1 - y0) / 4);
                var stepX = Math.Max(1, (x1 - x0) / 4);
                for (var y = y0; y < y1; y += stepY)
                {
                    var row = y * stride;
                    for (var x = x0; x < x1; x += stepX)
                    {
                        var i = row + x * 4;
                        b += bgra[i];
                        g += bgra[i + 1];
                        r += bgra[i + 2];
                        n++;
                    }
                }
                var o = (ty * targetWidth + tx) * 4;
                output[o] = (byte)(b / n);
                output[o + 1] = (byte)(g / n);
                output[o + 2] = (byte)(r / n);
                output[o + 3] = 255;
            }
        }
        return new BgraImage(output, targetWidth, targetHeight);
    }

    /// <summary>Mean Rec. 601 luma in [0, 255], as the web client measures it.</summary>
    public static double MeanLuma(BgraImage image)
    {
        double sum = 0;
        var pixels = image.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            sum += 0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i];
        }
        return sum / (pixels.Length / 4);
    }

    /// <summary>Three box-blur passes (close to a Gaussian), clamped at the edges.</summary>
    public static BgraImage Blur(BgraImage image, int radius)
    {
        if (radius <= 0) return image;
        var current = (byte[])image.Pixels.Clone();
        var scratch = new byte[current.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxPass(current, scratch, image.Width, image.Height, radius, horizontal: true);
            BoxPass(scratch, current, image.Width, image.Height, radius, horizontal: false);
        }
        return image with { Pixels = current };
    }

    private static void BoxPass(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var window = radius * 2 + 1;
        for (var line = 0; line < lines; line++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var sum = 0;
                for (var k = -radius; k <= radius; k++) sum += source[Index(line, Math.Clamp(k, 0, length - 1)) + channel];
                for (var p = 0; p < length; p++)
                {
                    target[Index(line, p) + channel] = (byte)(sum / window);
                    sum += source[Index(line, Math.Min(p + radius + 1, length - 1)) + channel];
                    sum -= source[Index(line, Math.Max(p - radius, 0)) + channel];
                }
            }
            for (var p = 0; p < length; p++) target[Index(line, p) + 3] = 255;
        }

        int Index(int l, int p) => horizontal ? (l * width + p) * 4 : (p * width + l) * 4;
    }
}
