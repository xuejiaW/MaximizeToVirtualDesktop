using System.Diagnostics;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

internal static class WindowEligibility
{
    public static bool IsApplicationWindow(IntPtr hwnd, AppSettings settings, bool allowOwnProcess = false)
    {
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return false;
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd) return false;
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return false;
        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((style & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0) return false;
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out int processId);
        if (processId == 0 || (!allowOwnProcess && processId == Environment.ProcessId)) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !(settings.ExcludedProcesses ?? []).Any(name =>
                string.Equals(Path.GetFileNameWithoutExtension(name.Trim()), process.ProcessName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
