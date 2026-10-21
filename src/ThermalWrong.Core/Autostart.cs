// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.Diagnostics;

namespace ThermalWrong.Core;

/// <summary>
/// Starts ThermalWrong at logon through an elevated scheduled task (the way TRCC does it), so it can run
/// the sensor helper and stop TRCC without UAC prompts. While enabled, TRCC's own logon task is
/// disabled so it does not grab the screen first; disabling puts TRCC's task back.
/// </summary>
public static class Autostart
{
    public const string TaskName = "ThermalWrong";
    const string TrccTask = "TRCCAppStartup";

    public static bool IsEnabled
    {
        get
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\" /fo csv /nh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0 && !output.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>One UAC prompt. Returns false if declined.</summary>
    public static bool Enable(string exePath)
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string dir = Path.GetDirectoryName(exePath)!;
        string script = $$"""
            $action = New-ScheduledTaskAction -Execute '{{Q(exePath)}}' -Argument '--tray' -WorkingDirectory '{{Q(dir)}}'
            $trigger = New-ScheduledTaskTrigger -AtLogOn -User '{{Q(user)}}'
            $principal = New-ScheduledTaskPrincipal -UserId '{{Q(user)}}' -LogonType Interactive -RunLevel Highest
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -Priority 4
            Register-ScheduledTask -TaskName '{{TaskName}}' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'ThermalWrong: plays media and temperatures on the Thermalright LCD.' -Force | Out-Null
            Disable-ScheduledTask -TaskName '{{TrccTask}}' -ErrorAction SilentlyContinue | Out-Null
            """;
        return Trcc.RunElevated(script) && IsEnabled;
    }

    /// <summary>
    /// One UAC prompt. Removes our task and, only if ours existed (so we were the ones who turned
    /// it off), re-enables TRCC's logon task.
    /// </summary>
    public static bool Disable() => Trcc.RunElevated($"""
        if (Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue) {"{"}
            Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false
            Enable-ScheduledTask -TaskName '{TrccTask}' -ErrorAction SilentlyContinue | Out-Null
        {"}"}
        """) && !IsEnabled;

    static string Q(string s) => s.Replace("'", "''");
}
