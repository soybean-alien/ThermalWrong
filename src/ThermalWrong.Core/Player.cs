// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThermalWrong.Core;

public enum PlayerState { Idle, WaitingForScreen, Preparing, Playing, Released, Error }

/// <summary>
/// Owns the screen on a background thread: opens it (retrying while another program holds it),
/// decodes the chosen media once, then loops the cached frames at the clip's frame rate.
/// </summary>
public sealed class Player : IDisposable
{
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly object _gate = new();
    volatile bool _quit;
    string? _media;
    int? _rotation;
    bool _released;
    int _version;

    public PlayerState State { get; private set; } = PlayerState.Idle;
    public string Status { get; private set; } = "Starting";
    public PanelInfo? Panel { get; private set; }
    public double MeasuredFps { get; private set; }
    public FrameSet? Current { get; private set; }

    /// <summary>Drawn on every frame before sending (null = frames go out untouched).</summary>
    public IFrameOverlay? Overlay { get; set; }

    readonly object _lastGate = new();
    byte[]? _last;
    int _lastLength, _lastSerial;

    /// <summary>Increments with every frame sent; lets previews skip unchanged frames.</summary>
    public int FrameSerial => Volatile.Read(ref _lastSerial);

    /// <summary>A copy of the JPEG most recently sent to the screen, for previews.</summary>
    public byte[]? CopyLastJpeg()
    {
        lock (_lastGate)
            return _last == null ? null : _last.AsSpan(LcdProtocol.HeaderSize, _lastLength - LcdProtocol.HeaderSize).ToArray();
    }

    public Player()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ThermalWrong player", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Shows a video, GIF or image. Rotation null means the panel's default.</summary>
    public void Show(string? media, int? rotation)
    {
        lock (_gate)
        {
            _media = media;
            _rotation = rotation;
            _version++;
        }
        _wake.Set();
    }

    /// <summary>Releases the screen so another program (TRCC) can use it, or takes it back.</summary>
    public void SetReleased(bool released)
    {
        lock (_gate)
        {
            _released = released;
            _version++;
        }
        _wake.Set();
    }

    /// <summary>Blocks until the screen has been let go, after <see cref="SetReleased"/>(true).</summary>
    public bool WaitUntilReleased(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (State != PlayerState.Released && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(20);
        return State == PlayerState.Released;
    }

    void Set(PlayerState state, string status)
    {
        State = state;
        Status = status;
    }

    void Run()
    {
        timeBeginPeriod(1);
        LcdDevice? device = null;
        try
        {
            while (!_quit)
            {
                string? media; int? rotation; bool released; int version;
                lock (_gate) (media, rotation, released, version) = (_media, _rotation, _released, _version);

                if (released)
                {
                    device?.Dispose();
                    device = null;
                    Set(PlayerState.Released, "Screen handed to TRCC");
                    _wake.WaitOne();
                    continue;
                }

                if (device == null)
                {
                    try
                    {
                        device = LcdDevice.Open();
                        Panel = device.Panel;
                    }
                    catch (LcdBusyException)
                    {
                        Set(PlayerState.WaitingForScreen, Trcc.IsRunning ? "TRCC is using the screen" : "Screen is busy");
                        _wake.WaitOne(2000);
                        continue;
                    }
                    catch (IOException e)
                    {
                        Set(PlayerState.WaitingForScreen, e.Message);
                        _wake.WaitOne(2000);
                        continue;
                    }
                }

                if (media == null || !File.Exists(media))
                {
                    Set(PlayerState.Idle, media == null ? "Nothing selected" : $"File not found: {Path.GetFileName(media)}");
                    _wake.WaitOne();
                    continue;
                }

                int rot = rotation ?? device.Panel.DefaultRotation;
                var set = Current;
                if (set == null || set.Source != media || _decodedFor != (device.Panel.Width, device.Panel.Height, rot))
                {
                    Set(PlayerState.Preparing, $"Preparing {Path.GetFileName(media)}");
                    Current = set = null;
                    GC.Collect();
                    try
                    {
                        set = MediaDecoder.Decode(media, device.Panel.Width, device.Panel.Height, rot);
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
                    {
                        Set(PlayerState.Error, e.Message);
                        _wake.WaitOne();
                        continue;
                    }
                    Current = set;
                    _decodedFor = (device.Panel.Width, device.Panel.Height, rot);
                    // Let go of decode scratch memory before settling into the loop.
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
                }

                try
                {
                    PlayLoop(device, set, version, rot);
                }
                catch (IOException e)
                {
                    device.Dispose();
                    device = null;
                    Set(PlayerState.WaitingForScreen, e.Message);
                    _wake.WaitOne(1000);
                }
            }
        }
        finally
        {
            device?.Dispose();
            timeEndPeriod(1);
        }
    }

    (int, int, int) _decodedFor;

    void PlayLoop(LcdDevice device, FrameSet set, int version, int rotation)
    {
        var panel = device.Panel;
        byte[] composed = new byte[LcdProtocol.HeaderSize + LcdProtocol.MaxJpegBytes];
        Set(PlayerState.Playing, $"Playing {Path.GetFileName(set.Source)}");
        var packets = set.Packets;
        double period = 1.0 / set.Fps;
        var clock = Stopwatch.StartNew();
        long sent = 0;
        double fpsWindowStart = 0;
        int fpsFrames = 0;
        for (int i = 0; ; i = (i + 1) % packets.Count)
        {
            if (_quit || Volatile.Read(ref _version) != version) return;

            var overlay = Overlay;
            if (overlay == null)
            {
                device.Send(packets[i]);
                Remember(packets[i], packets[i].Length);
            }
            else
            {
                int length = overlay.Apply(packets[i], panel.Width, panel.Height, rotation, ref composed);
                device.Send(composed, length);
                Remember(composed, length);
            }
            sent++;
            fpsFrames++;

            double now = clock.Elapsed.TotalSeconds;
            if (now - fpsWindowStart >= 1)
            {
                MeasuredFps = fpsFrames / (now - fpsWindowStart);
                fpsWindowStart = now;
                fpsFrames = 0;
            }

            double wait = sent * period - now;
            if (wait > 0)
                _wake.WaitOne(TimeSpan.FromSeconds(wait));
            else if (wait < -0.25)
            {
                // Fell well behind (system stall): restart the schedule instead of rushing frames.
                clock.Restart();
                sent = 0;
                fpsWindowStart = 0;
                fpsFrames = 0;
            }
        }
    }

    void Remember(byte[] packet, int length)
    {
        lock (_lastGate)
        {
            // The overlay reuses its buffer, so keep our own copy only when a preview might ask.
            if (_last == null || _last.Length < length) _last = new byte[Math.Max(length, LcdProtocol.HeaderSize + LcdProtocol.MaxJpegBytes)];
            Buffer.BlockCopy(packet, 0, _last, 0, length);
            _lastLength = length;
            _lastSerial++;
        }
    }

    public void Dispose()
    {
        _quit = true;
        _wake.Set();
        _thread.Join(3000);
        _wake.Dispose();
    }

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint ms);
}
