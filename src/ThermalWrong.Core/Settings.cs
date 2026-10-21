// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.Text.Json;

namespace ThermalWrong.Core;

public sealed class Settings
{
    public string? MediaPath { get; set; }

    /// <summary>Null means the panel's default rotation.</summary>
    public int? Rotation { get; set; }

    public string? MediaFolder { get; set; }

    /// <summary>CPU and GPU temperatures drawn over the media.</summary>
    public bool ShowTemperatures { get; set; } = true;

    /// <summary>True when the user handed the screen to TRCC; the app then starts without taking it.</summary>
    public bool ScreenHandedToTrcc { get; set; }

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThermalWrong");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (JsonException)
        {
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}

