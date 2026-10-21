// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;

namespace ThermalWrong.Core;

/// <summary>
/// Reads temperatures the same way TRCC does: TRCC's bundled HWINFO.exe (HWiNFO SDK) writes every
/// sensor as text plus JSON into the shared memory "shareMemory_SysInfo" about once a second, and
/// TRCC picks named sensors from it (FormSystemInfo.GetSystemInfo, UCSystemInfo.timer_event).
/// </summary>
public sealed class SensorSource : IDisposable
{
    const string MapName = "shareMemory_SysInfo";
    const int MapSize = 2 * 1024 * 1024, ViewSize = 1024 * 1024;

    // TRCC's preference order (FormSystemInfo.GetSystemInfo cases 1 and 4).
    static readonly string[] CpuNames = ["CPU", "CPU Package", "CPU Die (average)", "CPU (Tctl/Tdie)"];
    static readonly string[] GpuNames = ["GPU Temperature", "APU GFX"];

    readonly MemoryMappedFile _map;
    readonly MemoryMappedViewAccessor _view;
    readonly byte[] _buffer = new byte[ViewSize];
    readonly Timer _timer;
    DateTime _lastChange = DateTime.MinValue;
    int _lastHash;

    public int? CpuTemp { get; private set; }
    public int? GpuTemp { get; private set; }

    /// <summary>Raised on a timer thread when either reading changes.</summary>
    public event Action? Changed;

    public SensorSource()
    {
        // Holding the mapping open keeps it alive for HWINFO.exe even while TRCC is closed.
        _map = MemoryMappedFile.CreateOrOpen(MapName, MapSize);
        _view = _map.CreateViewAccessor(0, ViewSize, MemoryMappedFileAccess.Read);
        _timer = new Timer(_ => Poll(), null, 0, 1000);
    }

    public bool HelperRunning => Trcc.SensorHelperRunning;

    void Poll()
    {
        int? cpu = CpuTemp, gpu = GpuTemp;
        try
        {
            _view.ReadArray(0, _buffer, 0, _buffer.Length);
            int len = Array.IndexOf(_buffer, (byte)0);
            if (len < 0) len = _buffer.Length;
            string text = Encoding.ASCII.GetString(_buffer, 0, len);
            int hash = text.GetHashCode();
            if (hash != _lastHash)
            {
                _lastHash = hash;
                _lastChange = DateTime.UtcNow;
                var temps = ParseTemperatures(text);
                if (temps != null)
                {
                    cpu = Pick(temps, CpuNames);
                    gpu = Pick(temps, GpuNames);
                }
            }
            // HWINFO.exe gone or frozen: hide stale numbers rather than show old ones.
            if (DateTime.UtcNow - _lastChange > TimeSpan.FromSeconds(10)) cpu = gpu = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            cpu = gpu = null;
        }
        if (cpu != CpuTemp || gpu != GpuTemp)
        {
            CpuTemp = cpu;
            GpuTemp = gpu;
            Changed?.Invoke();
        }
    }

    /// <summary>Temperature sensors in document order, skipping per-core ones like TRCC does.</summary>
    static List<(string Name, float Value)>? ParseTemperatures(string text)
    {
        if (!text.StartsWith("HWi32_GetNumberOfDetectedSensors", StringComparison.Ordinal)) return null;
        int start = text.IndexOf('{');
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(text.AsMemory(start));
            var result = new List<(string, float)>();
            foreach (var group in doc.RootElement.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in group.Value.EnumerateArray())
                    foreach (var sensor in entry.EnumerateObject())
                    {
                        if (sensor.Value.GetProperty("type").GetString() != "temperature") continue;
                        if (sensor.Name.Contains("core ", StringComparison.OrdinalIgnoreCase)) continue;
                        if (float.TryParse(sensor.Value.GetProperty("value").GetString(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float v))
                            result.Add((sensor.Name, v));
                    }
            }
            return result;
        }
        catch (JsonException)
        {
            return null; // Caught HWINFO mid-write; keep the previous values.
        }
    }

    static int? Pick(List<(string Name, float Value)> temps, string[] names)
    {
        foreach (var name in names)
            foreach (var t in temps)
                if (t.Name == name) return (int)t.Value; // TRCC truncates to whole degrees.
        return null;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _view.Dispose();
        _map.Dispose();
    }
}
