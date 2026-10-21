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
        while (!stop.Wait(2000) && (seconds == 0 || clock.Elapsed.TotalSeconds < seconds))
        {
            self.Refresh();
            var set = player.Current;
            Console.WriteLine($"{clock.Elapsed:mm\\:ss}  {player.State,-16} {player.MeasuredFps,5:F1} fps  " +
                $"frames {set?.Packets.Count ?? 0} ({(set?.TotalBytes ?? 0) / 1048576.0:F1} MB)  " +
                $"cpu {self.TotalProcessorTime.TotalMilliseconds / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F2}%  " +
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
        int len = overlay.Apply(set.Packets[index % set.Packets.Count], 960, 540, 0, ref buffer);
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
            int len = overlay.Apply(set.Packets[i], 960, 540, 0, ref buffer);
            File.WriteAllBytes(Path.Combine(outDir, $"frame{n++:D4}.jpg"), buffer[LcdProtocol.HeaderSize..len]);
        }
        Console.WriteLine($"{n} frames at {set.Fps / every:F1} fps, CPU {sensors.CpuTemp} GPU {sensors.GpuTemp} -> {outDir}");
        break;
    }

    default:
        Console.WriteLine("Usage: ThermalWrong [probe | test | play <file> [--seconds N] [--overlay] | snapshot <file> <out.jpg> [frame]] [--rotate 0|90|180|270]");
        break;
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

