// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThermalWrong.App;

/// <summary>
/// One running copy. Launching it again (Start menu, desktop icon) opens the running copy's window.
/// The running copy is usually elevated (started by the logon task), and Windows blocks most
/// signals from a normal process to an elevated one, so it uses a registered window message that
/// the main window explicitly lets through.
/// </summary>
static class SingleInstance
{
    const string MutexName = @"Local\ThermalWrong.App";
    public static readonly uint ShowMessage = RegisterWindowMessage("ThermalWrong.Show");

    /// <summary>True if this is the only copy; the mutex must be kept alive while the app runs.</summary>
    public static bool TryClaim(out Mutex? mutex)
    {
        try
        {
            mutex = new Mutex(true, MutexName, out bool created);
            return created;
        }
        catch (UnauthorizedAccessException)
        {
            // Created by an elevated copy, which a normal process may not open: one is running.
            mutex = null;
            return false;
        }
    }

    /// <summary>Asks the running copy to show its window.</summary>
    public static void ShowRunningCopy()
    {
        int self = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self || !IsOurs(pid)) return true;
            PostMessage(hwnd, ShowMessage, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Called by the main window so the message gets through from non-elevated copies.</summary>
    public static void AllowShowMessage(IntPtr hwnd) => ChangeWindowMessageFilterEx(hwnd, ShowMessage, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);

    static bool IsOurs(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("ThermalWrong", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeInfo);
}
