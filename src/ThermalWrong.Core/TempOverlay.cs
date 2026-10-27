// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.Runtime.InteropServices;
using SkiaSharp;

namespace ThermalWrong.Core;

/// <summary>
/// Draws the CPU temperature top-left and the GPU temperature bottom-right in white, with no
/// panel behind them: only a soft shadow so they stay readable over bright water.
/// When the frame's layout is known, only the MCUs under the text are decoded, blended and re-encoded
/// (<see cref="JpegSplice"/>); otherwise the whole frame is decoded, drawn on and re-encoded.
/// </summary>
public sealed class TempOverlay : IFrameOverlay, IDisposable
{
    const int Margin = 30;
    const float NumberSize = 128, LabelSize = 24;
    // The logo medallion spans about 83% of the height, centred, so the corners keep this much width.
    const float CornerWidthOfHeight = 0.42f;

    readonly SensorSource _sensors;
    readonly SKTypeface _face = SKTypeface.FromFamilyName("Bahnschrift", new SKFontStyle(350, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
    readonly object _gate = new();
    SKBitmap? _frame;
    SKImage? _layer;
    (int?, int?, int, int) _layerKey;

    public TempOverlay(SensorSource sensors) => _sensors = sensors;

    public int Quality { get; set; } = 92;

    public int Apply(byte[] packet, JpegLayout? layout, int width, int height, int rotation, ref byte[] output)
    {
        lock (_gate)
        {
            bool sideways = rotation is 90 or 270;
            var layer = Layer(sideways ? height : width, sideways ? width : height);
            if (layout != null && layout.McusX == (width + 15) / 16 && layout.McusY == (height + 15) / 16)
            {
                var patch = PatchFor(layer, width, height, rotation);
                int length = Splice(packet, layout, patch, width, height, ref output);
                if (length - LcdProtocol.HeaderSize <= LcdProtocol.MaxJpegBytes) return length;
            }
            return Recompress(packet, layer, width, height, rotation, ref output);
        }
    }

    /// <summary>Copies the frame bit for bit except the MCUs the text touches, which are re-encoded with the text blended in.</summary>
    static int Splice(byte[] packet, JpegLayout l, Patch patch, int width, int height, ref byte[] output)
    {
        int head = LcdProtocol.HeaderSize + l.EntropyStart;
        int need = packet.Length + patch.Mcus.Length * 6 * 256 + 4096;
        if (output.Length < need) output = new byte[need + 64 * 1024];
        packet.AsSpan(0, head).CopyTo(output);
        var src = packet.AsSpan(head);
        var w = new BitWriter(output.AsSpan(head));
        Span<int> pred = stackalloc int[3]; // DC predictors of the stream being written
        int cur = 0, count = l.McuCount;
        bool inStep = true; // pred matches the source's predictors at cur
        for (int n = 0; n <= patch.Mcus.Length; n++)
        {
            int t = n < patch.Mcus.Length ? patch.Mcus[n] : count;
            if (cur < t)
            {
                // The first untouched MCU after a patched one codes its DCs against new predictors.
                if (!inStep) Reencode(src, l, cur++, pred, ref w, null, 0);
                if (cur < t) CopyBits(src, l, cur, t, ref w);
                for (int c = 0; c < 3; c++) pred[c] = l.McuPred[t * 3 + c];
                inStep = true;
            }
            if (t == count) break;
            Reencode(src, l, t, pred, ref w, patch, n);
            inStep = false;
            cur = t + 1;
        }
        w.Flush();
        int end = head + w.BytePosition();
        output[end] = 0xFF;
        output[end + 1] = 0xD9;
        LcdProtocol.WritePictureHeader(output, end + 2 - LcdProtocol.HeaderSize, width, height);
        return end + 2;
    }

    static void CopyBits(ReadOnlySpan<byte> src, JpegLayout l, int from, int to, ref BitWriter w)
    {
        var r = new BitReader(src, l.McuByte[from], l.McuBit[from] & 7);
        int n = l.McuBit[to] - l.McuBit[from];
        for (; n >= 32; n -= 32) w.Put((uint)r.GetBits(32), 32);
        if (n > 0) w.Put((uint)r.GetBits(n), n);
    }

    /// <summary>Decodes MCU m, blends the text into the blocks it covers (when a patch is given) and writes it out.</summary>
    static void Reencode(ReadOnlySpan<byte> src, JpegLayout l, int m, scoped Span<int> pred, ref BitWriter w, Patch? patch, int index)
    {
        var r = new BitReader(src, l.McuByte[m], l.McuBit[m] & 7);
        var t = l.Tables;
        Span<short> zz = stackalloc short[6 * 64];
        Span<int> srcPred = stackalloc int[3];
        for (int c = 0; c < 3; c++) srcPred[c] = l.McuPred[m * 3 + c];
        for (int b = 0; b < 6; b++)
        {
            int c = b < 4 ? 0 : b - 3, h = c == 0 ? 0 : 2;
            JpegSplice.DecodeBlock(ref r, zz.Slice(b * 64, 64), ref srcPred[c], t.Dec[h], t.Dec[h + 1]);
        }
        if (patch != null)
            for (int b = 0; b < 6; b++)
                if (patch.Mixed[index * 6 + b])
                {
                    // Source-over in YCbCr: blending is linear, so it matches blending in RGB and converting.
                    int c = b < 4 ? 0 : b - 3, o = (index * 6 + b) * 64;
                    JpegSplice.BlendBlock(zz.Slice(b * 64, 64), l.Dequant[c], l.Reciprocal[c], patch.Keep.Slice(o, 64), patch.Add.Slice(o, 64));
                }
        for (int b = 0; b < 6; b++)
        {
            int c = b < 4 ? 0 : b - 3, h = c == 0 ? 0 : 2;
            JpegSplice.EncodeBlock(ref w, zz.Slice(b * 64, 64), ref pred[c], t.Enc[h], t.Enc[h + 1]);
        }
    }

    /// <summary>The MCUs the text layer touches and, per 8x8 block, how much of the frame to keep and what to add.</summary>
    sealed class Patch
    {
        public (SKImage?, int, int, int) Key;
        public readonly List<int> McuList = [];
        public readonly List<bool> MixedList = []; // 6 blocks per MCU: Y0..Y3, Cb, Cr
        public readonly List<float> KeepList = [], AddList = []; // 64 per block, in level-shifted samples
        public ReadOnlySpan<int> Mcus => CollectionsMarshal.AsSpan(McuList);
        public ReadOnlySpan<bool> Mixed => CollectionsMarshal.AsSpan(MixedList);
        public ReadOnlySpan<float> Keep => CollectionsMarshal.AsSpan(KeepList);
        public ReadOnlySpan<float> Add => CollectionsMarshal.AsSpan(AddList);
    }

    readonly Patch _patch = new();
    SKBitmap? _turned;

    /// <summary>Rebuilt only when the text layer changes; the buffers are reused so readings don't churn memory.</summary>
    Patch PatchFor(SKImage? layer, int width, int height, int rotation)
    {
        var key = (layer, width, height, rotation);
        if (key == _patch.Key) return _patch;
        _patch.Key = key;
        _patch.McuList.Clear();
        _patch.MixedList.Clear();
        _patch.KeepList.Clear();
        _patch.AddList.Clear();
        if (layer == null) return _patch;

        if (_turned == null || _turned.Width != width || _turned.Height != height)
        {
            _turned?.Dispose();
            _turned = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        }
        using (var canvas = new SKCanvas(_turned))
        {
            canvas.Clear(SKColors.Transparent);
            Orient(canvas, width, height, rotation);
            canvas.DrawImage(layer, 0, 0);
        }
        var px = _turned.GetPixelSpan();

        Span<float> keep = stackalloc float[64], add = stackalloc float[64];
        int mx = (width + 15) / 16, my = (height + 15) / 16;
        for (int m = 0; m < mx * my; m++)
        {
            int x0 = m % mx * 16, y0 = m / mx * 16;
            bool touched = false;
            for (int y = y0; y < Math.Min(y0 + 16, height) && !touched; y++)
                for (int x = x0; x < Math.Min(x0 + 16, width); x++)
                    if (px[(y * width + x) * 4 + 3] != 0) { touched = true; break; }
            if (!touched) continue;
            _patch.McuList.Add(m);
            for (int blk = 0; blk < 6; blk++)
            {
                bool any = false;
                for (int i = 0; i < 64; i++)
                {
                    float k = 0, v = 0;
                    if (blk < 4)
                    {
                        int x = x0 + (blk & 1) * 8 + i % 8, y = y0 + (blk >> 1) * 8 + i / 8;
                        var (pk, py, _, _) = Sample(px, x, y, width, height);
                        k = pk;
                        v = py - 128 * (1 - pk);
                    }
                    else
                    {
                        // One chroma sample covers 2x2 pixels; the encoder averages them the same way.
                        for (int d = 0; d < 4; d++)
                        {
                            var (pk, _, cb, cr) = Sample(px, x0 + i % 8 * 2 + (d & 1), y0 + i / 8 * 2 + (d >> 1), width, height);
                            k += pk / 4;
                            v += (blk == 4 ? cb : cr) / 4;
                        }
                    }
                    keep[i] = k;
                    add[i] = v;
                    any |= k < 1;
                }
                _patch.MixedList.Add(any);
                _patch.KeepList.AddRange(keep);
                _patch.AddList.AddRange(add);
            }
        }
        return _patch;
    }

    /// <summary>The fraction of the frame a pixel keeps, and the text's premultiplied Y, Cb, Cr there (without offsets).</summary>
    static (float Keep, float Y, float Cb, float Cr) Sample(ReadOnlySpan<byte> px, int x, int y, int width, int height)
    {
        if (x >= width || y >= height) return (1, 0, 0, 0);
        int o = (y * width + x) * 4;
        float r = px[o], g = px[o + 1], b = px[o + 2];
        return (1 - px[o + 3] / 255f,
            0.299f * r + 0.587f * g + 0.114f * b,
            -0.168736f * r - 0.331264f * g + 0.5f * b,
            0.5f * r - 0.418688f * g - 0.081312f * b);
    }

    static void Orient(SKCanvas canvas, int width, int height, int rotation)
    {
        // Frames are already turned for how the panel is mounted; draw in the viewer's orientation.
        switch (rotation)
        {
            case 90: canvas.Translate(width, 0); canvas.RotateDegrees(90); break;
            case 180: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
            case 270: canvas.Translate(0, height); canvas.RotateDegrees(-90); break;
        }
    }

    /// <summary>The exact composite (decoded frame plus text, no re-encode), for checking the splice against.</summary>
    public SKBitmap Render(byte[] packet, int width, int height, int rotation)
    {
        lock (_gate)
        {
            bool sideways = rotation is 90 or 270;
            Draw(packet, Layer(sideways ? height : width, sideways ? width : height), width, height, rotation);
            return _frame!.Copy();
        }
    }

    /// <summary>The slow path: decode the whole frame, draw the text on, re-encode it.</summary>
    int Recompress(byte[] packet, SKImage? layer, int width, int height, int rotation, ref byte[] output)
    {
        Draw(packet, layer, width, height, rotation);
        using var image = SKImage.FromPixels(_frame!.PeekPixels());
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, Quality);
        int total = LcdProtocol.HeaderSize + (int)jpeg.Size;
        if (output.Length < total) output = new byte[total + 64 * 1024];
        LcdProtocol.WritePictureHeader(output, (int)jpeg.Size, width, height);
        jpeg.AsSpan().CopyTo(output.AsSpan(LcdProtocol.HeaderSize));
        return total;
    }

    void Draw(byte[] packet, SKImage? layer, int width, int height, int rotation)
    {
        int jpegLen = packet.Length - LcdProtocol.HeaderSize;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        if (_frame == null || _frame.Width != width || _frame.Height != height)
        {
            _frame?.Dispose();
            _frame = new SKBitmap(info);
        }
        unsafe
        {
            fixed (byte* p = &packet[LcdProtocol.HeaderSize])
            {
                using var data = SKData.Create((IntPtr)p, jpegLen);
                using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Bad frame JPEG.");
                codec.GetPixels(info, _frame.GetPixels());
            }
        }

        using (var canvas = new SKCanvas(_frame))
        {
            Orient(canvas, width, height, rotation);
            if (layer != null) canvas.DrawImage(layer, 0, 0);
        }
    }

    /// <summary>The transparent text layer, redrawn only when a reading changes.</summary>
    SKImage? Layer(int w, int h)
    {
        var key = (_sensors.CpuTemp, _sensors.GpuTemp, w, h);
        if (_layer != null && key == _layerKey) return _layer;
        _layer?.Dispose();
        _layerKey = key;
        if (key.Item1 == null && key.Item2 == null) return _layer = null;

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        float maxWidth = h * CornerWidthOfHeight;
        if (key.Item1 is int cpu) DrawReading(c, $"{cpu}°", "CPU", Margin, Margin, maxWidth, alignRight: false, labelBelow: true);
        if (key.Item2 is int gpu) DrawReading(c, $"{gpu}°", "GPU", w - Margin, h - Margin, maxWidth, alignRight: true, labelBelow: false);
        return _layer = surface.Snapshot();
    }

    void DrawReading(SKCanvas c, string number, string label, float x, float y, float maxWidth, bool alignRight, bool labelBelow)
    {
        using var numFont = new SKFont(_face, NumberSize);
        using var labelFont = new SKFont(_face, LabelSize);
        float numWidth = numFont.MeasureText(number);
        if (numWidth > maxWidth) numFont.Size = NumberSize * maxWidth / numWidth; // e.g. 100°+ stays clear of the logo
        numFont.GetFontMetrics(out var nm);
        labelFont.GetFontMetrics(out var lm);
        float numCap = nm.CapHeight, labelCap = lm.CapHeight, gap = 14;

        // y is the top edge (label below) or the bottom edge (label above) of the whole block.
        float numBaseline, labelBaseline;
        if (labelBelow)
        {
            numBaseline = y + numCap;
            labelBaseline = numBaseline + gap + labelCap;
        }
        else
        {
            numBaseline = y;
            labelBaseline = y - numCap - gap;
        }

        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        // Two soft shadows, no backing shape: a wide faint one for bright water, a tight one for edges.
        using var wide = SKImageFilter.CreateDropShadow(0, 0, 14, 14, new SKColor(0, 8, 24, 120));
        using var tight = SKImageFilter.CreateDropShadow(0, 2, 3, 3, new SKColor(0, 0, 0, 150));
        paint.ImageFilter = SKImageFilter.CreateCompose(tight, wide);

        c.DrawText(number, alignRight ? x - numFont.MeasureText(number) : x, numBaseline, numFont, paint);
        paint.Color = SKColors.White.WithAlpha(215);
        // Letter-spaced caption.
        DrawSpaced(c, label, x, labelBaseline, labelFont, paint, spacing: 6, alignRight);
        paint.ImageFilter.Dispose();
    }

    static void DrawSpaced(SKCanvas c, string text, float x, float baseline, SKFont font, SKPaint paint, float spacing, bool alignRight)
    {
        float total = text.Sum(ch => font.MeasureText(ch.ToString())) + spacing * (text.Length - 1);
        float cx = alignRight ? x - total : x;
        foreach (char ch in text)
        {
            c.DrawText(ch.ToString(), cx, baseline, font, paint);
            cx += font.MeasureText(ch.ToString()) + spacing;
        }
    }

    public void Dispose()
    {
        _layer?.Dispose();
        _frame?.Dispose();
        _turned?.Dispose();
        _face.Dispose();
    }
}

/// <summary>Draws on top of each frame before it goes to the screen.</summary>
public interface IFrameOverlay
{
    /// <summary>Writes the finished packet into <paramref name="output"/> (grown if needed) and returns its length.</summary>
    int Apply(byte[] packet, JpegLayout? layout, int width, int height, int rotation, ref byte[] output);
}

