// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ThermalWrong.Core;

/// <summary>A clip decoded once into ready-to-send picture packets.</summary>
public sealed class FrameSet(IReadOnlyList<byte[]> packets, double fps, string source)
{
    public IReadOnlyList<byte[]> Packets { get; } = packets;
    public double Fps { get; } = fps;
    public string Source { get; } = source;
    public long TotalBytes { get; } = packets.Sum(p => (long)p.Length);
}

public static partial class MediaDecoder
{
    public static readonly string[] Extensions = [".mp4", ".mov", ".mkv", ".webm", ".avi", ".gif", ".png", ".jpg", ".jpeg", ".bmp", ".webp"];

    /// <summary>
    /// Decodes a video, GIF or image with ffmpeg into panel-sized JPEG packets, cropped to fill
    /// and rotated for how the panel is mounted. ffmpeg exits as soon as decoding is done.
    /// </summary>
    public static FrameSet Decode(string path, int width, int height, int rotation, CancellationToken ct = default)
    {
        for (int q = 2; ; q += 2)
        {
            var (packets, fps) = Run(path, width, height, rotation, q, ct);
            if (packets.Count == 0) throw new InvalidDataException($"No frames could be decoded from {Path.GetFileName(path)}.");
            if (q >= 12 || packets.All(p => p.Length - LcdProtocol.HeaderSize < LcdProtocol.MaxJpegBytes))
                return new FrameSet(packets, packets.Count == 1 ? 1 : fps, path);
        }
    }

    static (List<byte[]>, double) Run(string path, int width, int height, int rotation, int quality, CancellationToken ct)
    {
        // For 90/270 the content is composed at the swapped size, then turned to fit the panel.
        bool sideways = rotation is 90 or 270;
        int cw = sideways ? height : width, ch = sideways ? width : height;
        string turn = rotation switch { 90 => ",transpose=1", 180 => ",hflip,vflip", 270 => ",transpose=2", _ => "" };
        string vf = $"scale={cw}:{ch}:force_original_aspect_ratio=increase:flags=lanczos,crop={cw}:{ch}{turn}";

        var psi = new ProcessStartInfo(Ffmpeg.Path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-i", path, "-an", "-vf", vf, "-fps_mode", "passthrough",
                     "-pix_fmt", "yuvj420p", "-c:v", "mjpeg", "-q:v", quality.ToString(CultureInfo.InvariantCulture), "-f", "image2pipe", "-" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new IOException("Could not start ffmpeg.");
        using var kill = ct.Register(() => { try { proc.Kill(); } catch { } });
        var log = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        proc.BeginErrorReadLine();

        var packets = new List<byte[]>();
        var frame = new MemoryStream(256 * 1024);
        var buf = new byte[1 << 16];
        var stdout = proc.StandardOutput.BaseStream;
        int prev = -1, n;
        while ((n = stdout.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                frame.WriteByte(b);
                // FF D9 (end of image) cannot occur inside JPEG entropy data, so it safely splits frames.
                if (prev == 0xFF && b == 0xD9)
                {
                    packets.Add(LcdProtocol.BuildPicturePacket(frame.GetBuffer().AsSpan(0, (int)frame.Length), width, height));
                    frame.SetLength(0);
                    b = 0;
                }
                prev = b;
            }
        }
        proc.WaitForExit();
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0 && packets.Count == 0)
            throw new InvalidDataException($"ffmpeg could not read {Path.GetFileName(path)}:\n{log}");
        return (packets, ParseFps(log.ToString()));
    }

    static double ParseFps(string log)
    {
        var m = FpsRegex().Match(log);
        if (!m.Success) m = TbrRegex().Match(log);
        return m.Success && double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var fps) && fps is > 0 and <= 120 ? fps : 30;
    }

    [GeneratedRegex(@"Stream #0:\d+.*Video:.*?, ([\d.]+) fps")]
    private static partial Regex FpsRegex();

    [GeneratedRegex(@"Stream #0:\d+.*Video:.*?, ([\d.]+) tbr")]
    private static partial Regex TbrRegex();
}

public static class Ffmpeg
{
    static string? _path;

    /// <summary>ffmpeg.exe from next to the app, PATH, or the TRCC install (which ships one).</summary>
    public static string Path => _path ??= Find() ?? throw new FileNotFoundException(
        "ffmpeg.exe not found. Put it next to ThermalWrong or on PATH, or install TRCC.");

    static string? Find()
    {
        var dirs = new List<string> { AppContext.BaseDirectory };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Trcc.InstallDir);
        return dirs.Select(d => System.IO.Path.Combine(d, "ffmpeg.exe")).FirstOrDefault(File.Exists);
    }
}
