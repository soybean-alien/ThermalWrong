// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using ThermalWrong.Core;

namespace ThermalWrong.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Used by the installer (already elevated, so no prompt): register or remove the logon task.
        if (args.Contains("--install-autostart", StringComparer.OrdinalIgnoreCase))
            return Autostart.Enable(Environment.ProcessPath!) ? 0 : 1;
        if (args.Contains("--remove-autostart", StringComparer.OrdinalIgnoreCase))
            return Autostart.Disable() ? 0 : 1;

        bool startHidden = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (!SingleInstance.TryClaim(out var mutex))
        {
            // Already running in the tray: bring its window up instead (unless this is a logon start).
            if (!startHidden) SingleInstance.ShowRunningCopy();
            return 0;
        }

        using (mutex)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(startHidden));
        }
        return 0;
    }
}
