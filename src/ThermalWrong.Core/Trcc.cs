// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.ComponentModel;
using System.Diagnostics;

namespace ThermalWrong.Core;

/// <summary>Starts and stops the vendor app so the screen can be handed back and forth.</summary>
public static class Trcc
{
    static readonly string[] ProcessNames = ["TRCC", "USBLCDNEW", "USBLCD"];
    const string StartupTask = "TRCCAppStartup";

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ThermalRight-TRCC", "TRCCCAP");

    public static bool IsRunning => ProcessNames.Any(n =>
    {
        var ps = Process.GetProcessesByName(n);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    });

    /// <summary>TRCC's sensor helper (HWiNFO SDK), which feeds the temperatures.</summary>
    public static bool SensorHelperRunning
    {
        get
        {
            var ps = Process.GetProcessesByName("HWINFO");
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }
    }

    // Starts TRCC's HWINFO.exe if it is not running. It needs admin for its sensor driver.
    static string StartHelperCommand => SensorHelperRunning ? "" :
        $"if (-not (Get-Process HWINFO -ErrorAction SilentlyContinue)) {{ Start-Process -FilePath '{Path.Combine(InstallDir, "HWINFO.exe")}' -WorkingDirectory '{InstallDir}' -WindowStyle Hidden }}";

    /// <summary>
    /// Stops TRCC and its USB helpers and makes sure the sensor helper keeps running, in one
    /// elevated step (TRCC runs as admin, so Windows asks for approval once).
    /// </summary>
    /// <returns>False if the user declined the prompt.</returns>
    public static bool Stop()
    {
        if (!IsRunning) return true;
        if (!RunElevated("Stop-Process -Name TRCC,USBLCDNEW,USBLCD -Force -ErrorAction SilentlyContinue; " + StartHelperCommand)) return false;
        for (int i = 0; i < 20 && IsRunning; i++) Thread.Sleep(100);
        return !IsRunning;
    }

    /// <summary>Starts the sensor helper on its own (one UAC prompt). False if declined or TRCC is not installed.</summary>
    public static bool StartSensorHelper()
    {
        if (SensorHelperRunning) return true;
        if (!File.Exists(Path.Combine(InstallDir, "HWINFO.exe"))) return false;
        return RunElevated(StartHelperCommand);
    }

    /// <summary>Runs a PowerShell script as admin. No prompt if this process is already elevated.</summary>
    internal static bool RunElevated(string command)
    {
        // -EncodedCommand avoids every quoting problem with paths and nested quotes.
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
        {
            return false;
        }
    }

    /// <summary>
    /// Starts TRCC through its own logon task, which already runs elevated, so no UAC prompt.
    /// Falls back to launching the exe (which prompts).
    /// </summary>
    public static void Start()
    {
        if (IsRunning) return;
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/run /tn \"{StartupTask}\"") { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            if (p.ExitCode == 0) return;
        }
        catch (Win32Exception)
        {
        }
        string exe = Path.Combine(InstallDir, "TRCC.exe");
        if (File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = InstallDir })?.Dispose();
    }
}
