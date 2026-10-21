// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using SkiaSharp;

namespace ThermalWrong.Core;

/// <summary>
/// Draws the CPU temperature top-left and the GPU temperature bottom-right in white, with no
/// panel behind them: only a soft shadow so they stay readable over bright water.
/// Each video frame is decoded, the cached text layer is drawn on, and the frame is re-encoded.
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

    public int Apply(byte[] packet, int width, int height, int rotation, ref byte[] output)
    {
        lock (_gate)
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

            bool sideways = rotation is 90 or 270;
            int dw = sideways ? height : width, dh = sideways ? width : height;
            var layer = Layer(dw, dh);
            using (var canvas = new SKCanvas(_frame))
            {
                // Frames are already turned for how the panel is mounted; draw in the viewer's orientation.
                switch (rotation)
                {
                    case 90: canvas.Translate(width, 0); canvas.RotateDegrees(90); break;
                    case 180: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
                    case 270: canvas.Translate(0, height); canvas.RotateDegrees(-90); break;
                }
                if (layer != null) canvas.DrawImage(layer, 0, 0);
            }

            using var image = SKImage.FromPixels(_frame.PeekPixels());
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, Quality);
            int total = LcdProtocol.HeaderSize + (int)jpeg.Size;
            if (output.Length < total) output = new byte[total + 64 * 1024];
            LcdProtocol.WritePictureHeader(output, (int)jpeg.Size, width, height);
            jpeg.AsSpan().CopyTo(output.AsSpan(LcdProtocol.HeaderSize));
            return total;
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
        _face.Dispose();
    }
}

/// <summary>Draws on top of each frame before it goes to the screen.</summary>
public interface IFrameOverlay
{
    /// <summary>Writes the finished packet into <paramref name="output"/> (grown if needed) and returns its length.</summary>
    int Apply(byte[] packet, int width, int height, int rotation, ref byte[] output);
}

