// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
// ThermalWrong command-line tool: diagnostics for the Thermalright USB LCD. The tray app is ThermalWrong.App.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ThermalWrong.Core;

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";
int? rotate = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--rotate") rotate = int.Parse(args[i + 1]);

switch (cmd)
{
    case "probe":
        using (var lcd = LcdDevice.Open())
            Console.WriteLine($"Screen: {lcd.Panel}, default rotation {lcd.Panel.DefaultRotation}");
        break;

    case "test":
        using (var lcd = LcdDevice.Open())
        using (var bmp = TestCard(lcd.Panel.Width, lcd.Panel.Height))
        {
            int rot = rotate ?? lcd.Panel.DefaultRotation;
            bmp.RotateFlip(rot switch { 90 => RotateFlipType.Rotate90FlipNone, 180 => RotateFlipType.Rotate180FlipNone, 270 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone });
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Jpeg);
            lcd.Send(LcdProtocol.BuildPicturePacket(ms.ToArray(), lcd.Panel.Width, lcd.Panel.Height));
            Console.WriteLine("Test card sent.");
        }
        break;

    case "play":
    {
        // Plays until Ctrl+C or the optional --seconds N, reporting fps and memory.
        int seconds = 0;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--seconds") seconds = int.Parse(args[i + 1]);
        using var player = new Player();
        using var sensors = args.Contains("--overlay") ? new SensorSource() : null;
        using var overlay = sensors != null ? new TempOverlay(sensors) : null;
        player.Overlay = overlay;
        player.Show(Path.GetFullPath(args[1]), rotate);
        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        var clock = Stopwatch.StartNew();
        var self = Process.GetCurrentProcess();
        var (lastCpu, lastWall) = (TimeSpan.Zero, TimeSpan.Zero);
        while (!stop.Wait(2000) && (seconds == 0 || clock.Elapsed.TotalSeconds < seconds))
        {
            self.Refresh();
            var set = player.Current;
            // Share of one core since the last line, which is what steady playback costs.
            double core = (self.TotalProcessorTime - lastCpu).TotalMilliseconds / (clock.Elapsed - lastWall).TotalMilliseconds * 100;
            (lastCpu, lastWall) = (self.TotalProcessorTime, clock.Elapsed);
            Console.WriteLine($"{clock.Elapsed:mm\\:ss}  {player.State,-16} {player.MeasuredFps,5:F1} fps  " +
                $"frames {set?.Packets.Count ?? 0} ({(set?.TotalBytes ?? 0) / 1048576.0:F1} MB)  " +
                $"cpu {self.TotalProcessorTime.TotalMilliseconds / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F2}%  " +
                $"now {core:F1}% of a core  " +
                $"temps {sensors?.CpuTemp}/{sensors?.GpuTemp}  private {self.PrivateMemorySize64 / 1048576.0:F1} MB  ws {self.WorkingSet64 / 1048576.0:F1} MB  {player.Status}");
        }
        break;
    }

    case "autostart":
    {
        // autostart on <path to the app exe> | autostart off
        bool ok = args[1] == "on" ? Autostart.Enable(Path.GetFullPath(args[2])) : Autostart.Disable();
        Console.WriteLine($"{(ok ? "Done" : "Declined or failed")}. ThermalWrong logon task enabled: {Autostart.IsEnabled}");
        break;
    }

    case "snapshot":
    {
        // Renders frame N of a clip with the temperature overlay to a JPEG, without touching the screen.
        int index = args.Length > 3 ? int.Parse(args[3]) : 0;
        var set = MediaDecoder.Decode(Path.GetFullPath(args[1]), 960, 540, 0);
        using var sensors = new SensorSource();
        Thread.Sleep(1500);
        using var overlay = new TempOverlay(sensors);
        var buffer = new byte[1];
        int len = overlay.Apply(set.Packets[index % set.Packets.Count], set.Layouts[index % set.Packets.Count], 960, 540, 0, ref buffer);
        File.WriteAllBytes(args[2], buffer[LcdProtocol.HeaderSize..len]);
        Console.WriteLine($"CPU {sensors.CpuTemp}  GPU {sensors.GpuTemp}  -> {args[2]}");
        break;
    }

    case "frames":
    {
        // Writes every Nth frame of a clip, with the live temperature overlay, as JPEGs (for the demo GIF).
        int every = 1;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--every") every = int.Parse(args[i + 1]);
        string outDir = Directory.CreateDirectory(args[2]).FullName;
        var set = MediaDecoder.Decode(Path.GetFullPath(args[1]), 960, 540, 0);
        using var sensors = new SensorSource();
        Thread.Sleep(1500);
        using var overlay = new TempOverlay(sensors);
        var buffer = new byte[1];
        int n = 0;
        for (int i = 0; i < set.Packets.Count; i += every)
        {
            int len = overlay.Apply(set.Packets[i], set.Layouts[i], 960, 540, 0, ref buffer);
            File.WriteAllBytes(Path.Combine(outDir, $"frame{n++:D4}.jpg"), buffer[LcdProtocol.HeaderSize..len]);
        }
        Console.WriteLine($"{n} frames at {set.Fps / every:F1} fps, CPU {sensors.CpuTemp} GPU {sensors.GpuTemp} -> {outDir}");
        break;
    }

    case "bench":
    {
        // Times the overlay per frame over a whole clip, spliced and fully re-encoded, without touching the screen,
        // and checks the spliced frames against a quality-100 render of the same composite. bench <file> [--save dir]
        int rot = rotate ?? 0;
        string file = Path.GetFullPath(args[1]);
        string? save = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--save") save = Directory.CreateDirectory(args[i + 1]).FullName;
        var clock = Stopwatch.StartNew();
        var plain = MediaDecoder.Decode(file, 960, 540, rot, repack: false);
        Console.WriteLine($"decode {clock.Elapsed.TotalSeconds:F1} s, {plain.TotalBytes / 1048576.0:F1} MB");
        clock.Restart();
        var set = MediaDecoder.Decode(file, 960, 540, rot);
        Console.WriteLine($"decode + repack {clock.Elapsed.TotalSeconds:F1} s, {set.TotalBytes / 1048576.0:F1} MB, " +
            $"{set.Layouts.Count(l => l != null)}/{set.Packets.Count} frames with a layout");

        // Repacking only re-codes the entropy data, so every pixel must come out the same.
        int changed = 0;
        for (int i = 0; i < set.Packets.Count; i++)
        {
            using var a = SkiaSharp.SKBitmap.Decode(plain.Packets[i][LcdProtocol.HeaderSize..]);
            using var b = SkiaSharp.SKBitmap.Decode(set.Packets[i][LcdProtocol.HeaderSize..]);
            if (!a.GetPixelSpan().SequenceEqual(b.GetPixelSpan())) changed++;
        }
        Console.WriteLine($"repacked frames that decode differently: {changed}");

        using var sensors = new SensorSource();
        Thread.Sleep(1500);
        using var overlay = new TempOverlay(sensors);
        var buffer = new byte[1];
        var none = new JpegLayout?[set.Packets.Count];
        foreach (var (name, layouts) in new[] { ("splice", set.Layouts), ("full re-encode", (IReadOnlyList<JpegLayout?>)none) })
            for (int pass = 0; pass < 3; pass++)
            {
                var sw = Stopwatch.StartNew();
                var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                long bytes = 0;
                for (int i = 0; i < set.Packets.Count; i++) bytes += overlay.Apply(set.Packets[i], layouts[i], 960, 540, rot, ref buffer);
                double ms = sw.Elapsed.TotalMilliseconds / set.Packets.Count;
                double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / set.Packets.Count;
                Console.WriteLine($"{name} pass {pass}: {ms:F3} ms/frame wall, {cpuMs:F3} ms/frame cpu = {cpuMs * set.Fps / 10:F1}% of a core " +
                    $"at {set.Fps:F0} fps, {bytes / set.Packets.Count / 1024} KB/frame (CPU {sensors.CpuTemp} GPU {sensors.GpuTemp})");
            }

        // Quality: the splice and the old quality-92 path, each against the exact composite (no JPEG round trip).
        double worstNew = double.MaxValue, worstOld = double.MaxValue, sumNew = 0, sumOld = 0;
        int maxDiff = 0, maxOld = 0, compared = 0;
        for (int i = 0; i < set.Packets.Count; i++)
        {
            var temps = (sensors.CpuTemp, sensors.GpuTemp);
            using var reference = overlay.Render(set.Packets[i], 960, 540, rot);
            using var old = Render(overlay.Apply(set.Packets[i], null, 960, 540, rot, ref buffer), buffer);
            int len = overlay.Apply(set.Packets[i], set.Layouts[i], 960, 540, rot, ref buffer);
            if (save != null && i % 30 == 0) File.WriteAllBytes(Path.Combine(save, $"splice{i:D4}.jpg"), buffer[LcdProtocol.HeaderSize..len]);
            using var spliced = Render(len, buffer);
            if (temps != (sensors.CpuTemp, sensors.GpuTemp)) continue; // a reading changed mid-frame
            compared++;
            var (psnrNew, diffNew) = Compare(spliced, reference);
            var (psnrOld, diffOld) = Compare(old, reference);
            maxOld = Math.Max(maxOld, diffOld);
            worstNew = Math.Min(worstNew, psnrNew); worstOld = Math.Min(worstOld, psnrOld);
            sumNew += psnrNew; sumOld += psnrOld;
            maxDiff = Math.Max(maxDiff, diffNew);
            if (save != null && i % 30 == 0) SkiaSharp.SKImage.FromBitmap(reference).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).SaveTo(File.Create(Path.Combine(save, $"reference{i:D4}.png")));
        }
        Console.WriteLine($"PSNR vs the exact composite over {compared} frames: splice average {sumNew / compared:F2} dB (worst {worstNew:F2}, " +
            $"largest channel difference {maxDiff}); old quality-92 path average {sumOld / compared:F2} dB (worst {worstOld:F2}, largest channel difference {maxOld})");
        break;
    }

    default:
        Console.WriteLine("Usage: ThermalWrong [probe | test | play <file> [--seconds N] [--overlay] | snapshot <file> <out.jpg> [frame]] [--rotate 0|90|180|270]");
        break;
}

static SkiaSharp.SKBitmap Render(int length, byte[] packet) =>
    SkiaSharp.SKBitmap.Decode(packet[LcdProtocol.HeaderSize..length], new SkiaSharp.SKImageInfo(960, 540, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque));

static (double Psnr, int MaxDiff) Compare(SkiaSharp.SKBitmap a, SkiaSharp.SKBitmap b)
{
    var pa = a.GetPixelSpan();
    var pb = b.GetPixelSpan();
    double sse = 0;
    int max = 0, n = 0;
    for (int i = 0; i < pa.Length; i++)
    {
        if (i % 4 == 3) continue; // alpha
        int d = pa[i] - pb[i];
        sse += d * d;
        max = Math.Max(max, Math.Abs(d));
        n++;
    }
    return (sse == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * n / sse), max);
}

static Bitmap TestCard(int w, int h)
{
    var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(20, 10, 60), Color.FromArgb(0, 120, 140), 0f))
        g.FillRectangle(bg, 0, 0, w, h);
    // Corner markers show orientation: red = top-left, green = top-right, blue = bottom-left.
    int m = Math.Min(w, h) / 6;
    g.FillRectangle(Brushes.Red, 0, 0, m, m);
    g.FillRectangle(Brushes.Lime, w - m, 0, m, m);
    g.FillRectangle(Brushes.Blue, 0, h - m, m, m);
    using var font = new Font("Segoe UI", Math.Min(w, h) / 8f, FontStyle.Bold, GraphicsUnit.Pixel);
    using var small = new Font("Segoe UI", Math.Min(w, h) / 16f, GraphicsUnit.Pixel);
    var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    g.DrawString("Hello from ThermalWrong", font, Brushes.White, new RectangleF(0, 0, w, h * 0.7f), center);
    g.DrawString($"{w}x{h}  {DateTime.Now:HH:mm:ss}", small, Brushes.White, new RectangleF(0, h * 0.5f, w, h * 0.5f), center);
    return bmp;
}

