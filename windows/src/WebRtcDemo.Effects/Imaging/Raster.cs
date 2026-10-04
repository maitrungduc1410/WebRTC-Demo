namespace WebRtcDemo.Effects.Imaging;

/// <summary>
/// CPU pixel operations on BGRA. Every per-frame operation writes into caller-owned buffers;
/// lookup tables live in <see cref="ResampleTable"/> / <see cref="BlurKernel"/> instances that are
/// rebuilt only when sizes change.
/// </summary>
public static unsafe class Raster
{
    /// <summary>Box-filters <paramref name="src"/> into <paramref name="dst"/> (any sizes; meant for shrinking).</summary>
    public static void ResizeArea(BgraBitmap src, BgraBitmap dst) =>
        ResizeArea(src.Pixels, src.Width, src.Height, src.Stride, dst);

    public static void ResizeArea(ReadOnlySpan<byte> src, int sw, int sh, int stride, BgraBitmap dst)
    {
        int dw = dst.Width, dh = dst.Height;
        Span<int> xs = dw <= 4096 ? stackalloc int[dw + 1] : new int[dw + 1];
        for (var x = 0; x <= dw; x++) xs[x] = (int)((long)x * sw / dw);
        fixed (byte* s = src)
        fixed (byte* d = dst.Pixels)
        {
            for (var y = 0; y < dh; y++)
            {
                var y0 = (int)((long)y * sh / dh);
                var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sh / dh));
                var o = d + y * dw * 4;
                for (var x = 0; x < dw; x++)
                {
                    var x0 = xs[x];
                    var x1 = Math.Max(x0 + 1, xs[x + 1]);
                    uint b = 0, g = 0, r = 0, a = 0;
                    for (var yy = y0; yy < y1; yy++)
                    {
                        var p = s + yy * stride + x0 * 4;
                        for (var xx = x0; xx < x1; xx++, p += 4)
                        {
                            b += p[0];
                            g += p[1];
                            r += p[2];
                            a += p[3];
                        }
                    }
                    var n = (uint)((x1 - x0) * (y1 - y0));
                    var half = n / 2;
                    o[0] = (byte)((b + half) / n);
                    o[1] = (byte)((g + half) / n);
                    o[2] = (byte)((r + half) / n);
                    o[3] = (byte)((a + half) / n);
                    o += 4;
                }
            }
        }
    }

    /// <summary>
    /// Bilinear resample through <paramref name="table"/> (stretch or cover, see
    /// <see cref="ResampleTable.Configure"/>). Alpha is set to 255.
    /// </summary>
    public static void Resample(BgraBitmap src, BgraBitmap dst, ResampleTable table)
    {
        fixed (byte* s = src.Pixels)
        fixed (byte* d = dst.Pixels)
        fixed (int* xi = table.X)
        fixed (int* xw = table.XWeight)
        {
            int sw4 = src.Stride, dw = dst.Width;
            for (var y = 0; y < dst.Height; y++)
            {
                var row0 = s + table.Y[y] * sw4;
                var row1 = s + Math.Min(table.Y[y] + 1, src.Height - 1) * sw4;
                var wy = table.YWeight[y];
                var o = d + y * dw * 4;
                for (var x = 0; x < dw; x++, o += 4)
                {
                    var i0 = xi[x] * 4;
                    var i1 = Math.Min(xi[x] + 1, src.Width - 1) * 4;
                    var wx = xw[x];
                    for (var c = 0; c < 3; c++)
                    {
                        var top = row0[i0 + c] * (256 - wx) + row0[i1 + c] * wx;
                        var bottom = row1[i0 + c] * (256 - wx) + row1[i1 + c] * wx;
                        o[c] = (byte)((top * (256 - wy) + bottom * wy + 32768) >> 16);
                    }
                    o[3] = 255;
                }
            }
        }
    }

    /// <summary>Separable Gaussian, clamped at the edges; <paramref name="temp"/> is resized to match.</summary>
    public static void GaussianBlur(BgraBitmap image, BlurKernel kernel, BgraBitmap temp)
    {
        temp.Resize(image.Width, image.Height);
        BlurPass(image.Pixels, temp.Pixels, image.Width, image.Height, kernel, horizontal: true);
        BlurPass(temp.Pixels, image.Pixels, image.Width, image.Height, kernel, horizontal: false);
    }

    private static void BlurPass(byte[] src, byte[] dst, int w, int h, BlurKernel kernel, bool horizontal)
    {
        var radius = kernel.Radius;
        fixed (byte* s = src)
        fixed (byte* d = dst)
        fixed (int* k = kernel.Weights)
        {
            var lines = horizontal ? h : w;
            var length = horizontal ? w : h;
            var step = horizontal ? 4 : w * 4;
            for (var line = 0; line < lines; line++)
            {
                var start = horizontal ? line * w * 4 : line * 4;
                for (var i = 0; i < length; i++)
                {
                    int b = 0, g = 0, r = 0;
                    for (var t = -radius; t <= radius; t++)
                    {
                        var j = Math.Clamp(i + t, 0, length - 1);
                        var p = s + start + j * step;
                        var weight = k[t + radius];
                        b += p[0] * weight;
                        g += p[1] * weight;
                        r += p[2] * weight;
                    }
                    var o = d + start + i * step;
                    o[0] = (byte)((b + 32768) >> 16);
                    o[1] = (byte)((g + 32768) >> 16);
                    o[2] = (byte)((r + 32768) >> 16);
                    o[3] = 255;
                }
            }
        }
    }

    /// <summary>
    /// <paramref name="frame"/> = mix(background, frame, alpha) in place, where alpha is the person
    /// mask (bilinear from its own resolution through <paramref name="maskTable"/>, then through the
    /// <paramref name="edge"/> curve) and the background is sampled bilinearly through
    /// <paramref name="backgroundTable"/>, only where it shows.
    /// </summary>
    public static void Composite(
        BgraBitmap frame, BgraBitmap background, ResampleTable backgroundTable,
        byte[] mask, int maskWidth, int maskHeight, ResampleTable maskTable, byte[] edge)
    {
        fixed (byte* f = frame.Pixels)
        fixed (byte* bg = background.Pixels)
        fixed (byte* m = mask)
        fixed (byte* lut = edge)
        fixed (int* mx = maskTable.X)
        fixed (int* mxw = maskTable.XWeight)
        fixed (int* bx = backgroundTable.X)
        fixed (int* bxw = backgroundTable.XWeight)
        {
            int w = frame.Width, bw = background.Width, bh = background.Height, bStride = background.Stride;
            for (var y = 0; y < frame.Height; y++)
            {
                var my0 = maskTable.Y[y];
                var mrow0 = m + my0 * maskWidth;
                var mrow1 = m + Math.Min(my0 + 1, maskHeight - 1) * maskWidth;
                var mwy = maskTable.YWeight[y];
                var by0 = backgroundTable.Y[y];
                var brow0 = bg + by0 * bStride;
                var brow1 = bg + Math.Min(by0 + 1, bh - 1) * bStride;
                var bwy = backgroundTable.YWeight[y];
                var o = f + y * w * 4;
                for (var x = 0; x < w; x++, o += 4)
                {
                    var i0 = mx[x];
                    var i1 = Math.Min(i0 + 1, maskWidth - 1);
                    var wx = mxw[x];
                    var top = mrow0[i0] * (256 - wx) + mrow0[i1] * wx;
                    var bottom = mrow1[i0] * (256 - wx) + mrow1[i1] * wx;
                    var alpha = lut[(top * (256 - mwy) + bottom * mwy + 32768) >> 16];
                    if (alpha == 255) continue;

                    var j0 = bx[x] * 4;
                    var j1 = Math.Min(bx[x] + 1, bw - 1) * 4;
                    var bwx = bxw[x];
                    var inverse = 255 - alpha;
                    for (var c = 0; c < 3; c++)
                    {
                        var bt = brow0[j0 + c] * (256 - bwx) + brow0[j1 + c] * bwx;
                        var bb = brow1[j0 + c] * (256 - bwx) + brow1[j1 + c] * bwx;
                        var back = (bt * (256 - bwy) + bb * bwy + 32768) >> 16;
                        o[c] = (byte)((o[c] * alpha + back * inverse + 127) / 255);
                    }
                }
            }
        }
    }

    /// <summary>The smoothstep(low, high) edge of a 0..255 confidence, as a lookup table.</summary>
    public static byte[] EdgeTable(float low, float high)
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var t = Math.Clamp((i / 255f - low) / (high - low), 0f, 1f);
            table[i] = (byte)MathF.Round(t * t * (3 - 2 * t) * 255);
        }
        return table;
    }

    /// <summary>Draws a premultiplied-alpha sticker centered at the placement, rotated clockwise by its angle.</summary>
    public static void DrawSticker(BgraBitmap frame, BgraBitmap sticker, Placement placement)
    {
        if (placement.Width < 1 || placement.Height < 1) return;
        var cos = MathF.Cos(placement.Angle);
        var sin = MathF.Sin(placement.Angle);
        var halfW = placement.Width / 2;
        var halfH = placement.Height / 2;
        var extentX = MathF.Abs(halfW * cos) + MathF.Abs(halfH * sin);
        var extentY = MathF.Abs(halfW * sin) + MathF.Abs(halfH * cos);
        var x0 = Math.Max(0, (int)MathF.Floor(placement.X - extentX));
        var x1 = Math.Min(frame.Width, (int)MathF.Ceiling(placement.X + extentX));
        var y0 = Math.Max(0, (int)MathF.Floor(placement.Y - extentY));
        var y1 = Math.Min(frame.Height, (int)MathF.Ceiling(placement.Y + extentY));
        if (x0 >= x1 || y0 >= y1) return;

        int sw = sticker.Width, sh = sticker.Height;
        // Frame pixel -> sticker pixel: undo the translation and rotation, then scale.
        var su = sw / placement.Width;
        var sv = sh / placement.Height;
        fixed (byte* f = frame.Pixels)
        fixed (byte* s = sticker.Pixels)
        {
            for (var y = y0; y < y1; y++)
            {
                var dy = y + 0.5f - placement.Y;
                var o = f + (y * frame.Width + x0) * 4;
                for (var x = x0; x < x1; x++, o += 4)
                {
                    var dx = x + 0.5f - placement.X;
                    var u = (dx * cos + dy * sin + halfW) * su - 0.5f;
                    var v = (-dx * sin + dy * cos + halfH) * sv - 0.5f;
                    if (u <= -1 || v <= -1 || u >= sw || v >= sh) continue;
                    var ix = (int)MathF.Floor(u);
                    var iy = (int)MathF.Floor(v);
                    var fx = (int)((u - ix) * 256);
                    var fy = (int)((v - iy) * 256);
                    // Texels outside the artwork are transparent, so its edges fade over one pixel.
                    uint p00 = Texel(s, sw, sh, ix, iy), p10 = Texel(s, sw, sh, ix + 1, iy);
                    uint p01 = Texel(s, sw, sh, ix, iy + 1), p11 = Texel(s, sw, sh, ix + 1, iy + 1);
                    if ((p00 | p10 | p01 | p11) == 0) continue;
                    var alpha = Bilinear(p00, p10, p01, p11, 24, fx, fy);
                    if (alpha == 0) continue;
                    var keep = 255 - alpha;
                    for (var c = 0; c < 3; c++)
                    {
                        var color = Bilinear(p00, p10, p01, p11, c * 8, fx, fy);
                        o[c] = (byte)Math.Min(255, color + (o[c] * keep + 127) / 255);
                    }
                }
            }
        }
    }

    private static int Bilinear(uint p00, uint p10, uint p01, uint p11, int shift, int fx, int fy)
    {
        var top = (int)((p00 >> shift) & 0xFF) * (256 - fx) + (int)((p10 >> shift) & 0xFF) * fx;
        var bottom = (int)((p01 >> shift) & 0xFF) * (256 - fx) + (int)((p11 >> shift) & 0xFF) * fx;
        return (top * (256 - fy) + bottom * fy + 32768) >> 16;
    }

    private static uint Texel(byte* s, int w, int h, int x, int y) =>
        x < 0 || y < 0 || x >= w || y >= h ? 0 : *(uint*)(s + (y * w + x) * 4);

    /// <summary>BT.601 limited-range I420, chroma averaged over 2x2 blocks (what libyuv's ARGBToI420 does).</summary>
    public static void BgraToI420(BgraBitmap src, I420Buffer dst)
    {
        int w = src.Width, h = src.Height;
        dst.Resize(w, h);
        var cw = dst.StrideUV;
        fixed (byte* s = src.Pixels)
        fixed (byte* py = dst.Y)
        fixed (byte* pu = dst.U)
        fixed (byte* pv = dst.V)
        {
            for (var y = 0; y < h; y += 2)
            {
                var r0 = s + y * w * 4;
                var r1 = y + 1 < h ? r0 + w * 4 : r0;
                var y0 = py + y * w;
                var y1 = y + 1 < h ? y0 + w : null;
                var u = pu + (y / 2) * cw;
                var v = pv + (y / 2) * cw;
                for (var x = 0; x < w; x += 2)
                {
                    var x1 = x + 1 < w ? x + 1 : x;
                    var a = r0 + x * 4;
                    var b = r0 + x1 * 4;
                    var c = r1 + x * 4;
                    var d = r1 + x1 * 4;
                    y0[x] = Luma(a);
                    if (x + 1 < w) y0[x + 1] = Luma(b);
                    if (y1 != null)
                    {
                        y1[x] = Luma(c);
                        if (x + 1 < w) y1[x + 1] = Luma(d);
                    }
                    var bb = (a[0] + b[0] + c[0] + d[0] + 2) >> 2;
                    var gg = (a[1] + b[1] + c[1] + d[1] + 2) >> 2;
                    var rr = (a[2] + b[2] + c[2] + d[2] + 2) >> 2;
                    u[x / 2] = (byte)(((-38 * rr - 74 * gg + 112 * bb + 128) >> 8) + 128);
                    v[x / 2] = (byte)(((112 * rr - 94 * gg - 18 * bb + 128) >> 8) + 128);
                }
            }
        }
    }

    private static byte Luma(byte* p) => (byte)(((66 * p[2] + 129 * p[1] + 25 * p[0] + 128) >> 8) + 16);

    /// <summary>
    /// Writes <paramref name="src"/> as an RGB NHWC float tensor: value = channel / 255 * scale + bias,
    /// placed at (<paramref name="left"/>, <paramref name="top"/>) of a <paramref name="size"/> square,
    /// with <paramref name="fill"/> everywhere else (letterboxing).
    /// </summary>
    public static void ToTensor(BgraBitmap src, Span<float> tensor, int size, int left, int top, float scale, float bias, float fill)
    {
        tensor[..(size * size * 3)].Fill(fill);
        var k = scale / 255f;
        fixed (byte* s = src.Pixels)
        fixed (float* t = tensor)
        {
            for (var y = 0; y < src.Height; y++)
            {
                var p = s + y * src.Stride;
                var o = t + ((y + top) * size + left) * 3;
                for (var x = 0; x < src.Width; x++, p += 4, o += 3)
                {
                    o[0] = p[2] * k + bias;
                    o[1] = p[1] * k + bias;
                    o[2] = p[0] * k + bias;
                }
            }
        }
    }

    /// <summary>
    /// Bilinear warp into a square RGB float tensor: tensor pixel (u, v) samples
    /// <paramref name="src"/> at origin + u * axisU + v * axisV (pixel centers), clamped at the edges.
    /// </summary>
    public static void WarpToTensor(BgraBitmap src, Span<float> tensor, int size, Point origin, Point axisU, Point axisV, float scale, float bias)
    {
        var k = scale / 255f / 65536f;
        int sw = src.Width, sh = src.Height;
        fixed (byte* s = src.Pixels)
        fixed (float* t = tensor)
        {
            var o = t;
            for (var v = 0; v < size; v++)
            {
                for (var u = 0; u < size; u++, o += 3)
                {
                    var x = origin.X + (u + 0.5f) * axisU.X + (v + 0.5f) * axisV.X - 0.5f;
                    var y = origin.Y + (u + 0.5f) * axisU.Y + (v + 0.5f) * axisV.Y - 0.5f;
                    x = Math.Clamp(x, 0, sw - 1);
                    y = Math.Clamp(y, 0, sh - 1);
                    var ix = Math.Min((int)x, sw - 2 < 0 ? 0 : sw - 2);
                    var iy = Math.Min((int)y, sh - 2 < 0 ? 0 : sh - 2);
                    var fx = (int)((x - ix) * 256);
                    var fy = (int)((y - iy) * 256);
                    var p0 = s + (iy * sw + ix) * 4;
                    var p1 = p0 + (sh > 1 ? sw * 4 : 0);
                    var dx = sw > 1 ? 4 : 0;
                    for (var c = 0; c < 3; c++)
                    {
                        var top = p0[c] * (256 - fx) + p0[c + dx] * fx;
                        var bottom = p1[c] * (256 - fx) + p1[c + dx] * fx;
                        o[2 - c] = (top * (256 - fy) + bottom * fy) * k + bias;
                    }
                }
            }
        }
    }
}

/// <summary>Source index and 8-bit weight per destination row and column, for bilinear resampling.</summary>
public sealed class ResampleTable
{
    private (int Sw, int Sh, int Dw, int Dh, bool Cover) _key;

    public int[] X { get; private set; } = [];
    public int[] XWeight { get; private set; } = [];
    public int[] Y { get; private set; } = [];
    public int[] YWeight { get; private set; } = [];

    /// <summary>
    /// Maps a <paramref name="dw"/> x <paramref name="dh"/> destination onto the source: stretched,
    /// or (with <paramref name="cover"/>) scaled to fill and center-cropped like CSS object-fit: cover.
    /// </summary>
    public ResampleTable Configure(int sw, int sh, int dw, int dh, bool cover)
    {
        var key = (sw, sh, dw, dh, cover);
        if (key == _key && X.Length == dw) return this;
        _key = key;
        double scaleX = (double)dw / sw, scaleY = (double)dh / sh;
        double offsetX = 0, offsetY = 0;
        if (cover)
        {
            scaleX = scaleY = Math.Max(scaleX, scaleY);
            offsetX = (sw - dw / scaleX) / 2;
            offsetY = (sh - dh / scaleY) / 2;
        }
        (X, XWeight) = Axis(sw, dw, scaleX, offsetX);
        (Y, YWeight) = Axis(sh, dh, scaleY, offsetY);
        return this;
    }

    private static (int[], int[]) Axis(int source, int destination, double scale, double offset)
    {
        var index = new int[destination];
        var weight = new int[destination];
        for (var i = 0; i < destination; i++)
        {
            var position = Math.Clamp((i + 0.5) / scale + offset - 0.5, 0, source - 1);
            var floor = (int)position;
            index[i] = floor;
            weight[i] = (int)Math.Round((position - floor) * 256);
        }
        return (index, weight);
    }
}

/// <summary>A normalized Gaussian kernel in 16.16 fixed point, three sigmas wide.</summary>
public sealed class BlurKernel
{
    public BlurKernel(float sigma)
    {
        Sigma = sigma;
        Radius = Math.Max(1, (int)MathF.Ceiling(sigma * 3));
        var weights = new double[Radius * 2 + 1];
        double sum = 0;
        for (var i = -Radius; i <= Radius; i++) sum += weights[i + Radius] = Math.Exp(-i * i / (2.0 * sigma * sigma));
        Weights = new int[weights.Length];
        var total = 0;
        for (var i = 0; i < weights.Length; i++) total += Weights[i] = (int)Math.Round(weights[i] / sum * 65536);
        Weights[Radius] += 65536 - total;
    }

    public float Sigma { get; }
    public int Radius { get; }
    public int[] Weights { get; }
}
