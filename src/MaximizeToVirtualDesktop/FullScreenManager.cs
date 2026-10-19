using System.Diagnostics;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

internal sealed class FullScreenManager(VirtualDesktopService desktops, FullScreenTracker tracker, AppSettings settings)
{
    private readonly VirtualDesktopService _vds = desktops;
    private readonly FullScreenTracker _tracker = tracker;
    private readonly AppSettings _settings = settings;
    private readonly HashSet<IntPtr> _inFlight = [];

    public bool IsBusy(IntPtr hwnd) => _inFlight.Contains(hwnd);
    public bool CanAutomaticallyManage(IntPtr hwnd) => !_vds.IsWindowPinned(hwnd)
        && _vds.GetDesktopIdForWindow(hwnd) is Guid id && id != Guid.Empty && id == _vds.GetCurrentDesktopId();

    public void Toggle(IntPtr hwnd)
    {
        if (_tracker.IsTracked(hwnd)) Restore(hwnd);
        else MaximizeToDesktop(hwnd);
    }

    public void MaximizeToDesktop(IntPtr hwnd)
    {
        // Enter is idempotent. Only the explicit Toggle operation may restore.
        if (!NativeMethods.IsWindow(hwnd) || _tracker.IsTracked(hwnd) || !_inFlight.Add(hwnd)) return;
        try { EnterCore(hwnd); }
        finally { _inFlight.Remove(hwnd); }
    }

    private void EnterCore(IntPtr hwnd)
    {
        var originalId = _vds.GetDesktopIdForWindow(hwnd);
        var originalPlacement = NativeMethods.WINDOWPLACEMENT.Default;
        if (originalId is null || originalId == Guid.Empty || !NativeMethods.GetWindowPlacement(hwnd, ref originalPlacement)) return;
        var (desktop, desktopId) = _vds.CreateDesktop();
        if (desktop == null || desktopId == null) return;
        bool tracked = false;
        try
        {
            if (!_vds.MoveWindowToDesktop(hwnd, desktop)) return;
            string processName = "Window";
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                using var process = Process.GetProcessById(pid);
                processName = string.IsNullOrWhiteSpace(process.MainWindowTitle) ? process.ProcessName : process.MainWindowTitle;
            }
            catch (ArgumentException) { }
            _vds.SetDesktopName(desktop, $"[MVD] {processName}");
            // Track before changing focus or size so event callbacks cannot enter twice.
            _tracker.Track(hwnd, originalId.Value, desktopId.Value, desktop, processName, originalPlacement);
            tracked = true;
            if (!_vds.SwitchToDesktop(desktop)) { RestoreCore(hwnd, true); return; }
            if (!NativeMethods.IsWindowElevated(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MAXIMIZE);
            NativeMethods.SetForegroundWindow(hwnd);
            if (_settings.ShowSwitchPopup) NotificationOverlay.ShowNotification("→ Virtual Desktop", processName, hwnd);
        }
        finally
        {
            if (!tracked)
            {
                _vds.RemoveDesktop(desktop);
                ComRelease.Release(desktop);
            }
        }
    }

    public void Restore(IntPtr hwnd, bool preserveWindowState = false)
    {
        if (!_tracker.IsTracked(hwnd) || !_inFlight.Add(hwnd)) return;
        try { RestoreCore(hwnd, preserveWindowState); }
        finally { _inFlight.Remove(hwnd); }
    }

    private void RestoreCore(IntPtr hwnd, bool preserveWindowState)
    {
        var entry = _tracker.Get(hwnd);
        if (entry == null) return;
        bool wasCurrent = _vds.GetCurrentDesktopId() == entry.TempDesktopId;
        var original = _vds.FindDesktop(entry.OriginalDesktopId);
        try
        {
            // A failed move must leave the tracking/recovery entry intact.
            if (original != null && NativeMethods.IsWindow(hwnd) && !_vds.MoveWindowToDesktop(hwnd, original)) return;
            if (!preserveWindowState && NativeMethods.IsWindow(hwnd))
            {
                var placement = entry.OriginalPlacement;
                placement.showCmd = NativeMethods.SW_SHOWNORMAL;
                NativeMethods.SetWindowPlacement(hwnd, ref placement);
                NativeMethods.ShowWindow(hwnd, (int)NativeMethods.SW_SHOWNORMAL);
            }
            if (wasCurrent && original != null) _vds.SwitchToDesktop(original);
            if (!FinishDesktop(entry, hwnd)) return;
            if (wasCurrent && NativeMethods.IsWindow(hwnd)) NativeMethods.SetForegroundWindow(hwnd);
        }
        finally { if (original != null) ComRelease.Release(original); }
    }

    private bool FinishDesktop(TrackingEntry entry, IntPtr ignoredWindow)
    {
        // Retain a desktop if the user placed another application there.
        bool occupied = _vds.HasApplicationWindows(entry.TempDesktopId, ignoredWindow);
        if (!occupied && !_vds.RemoveDesktop(entry.TempDesktop)) return false;
        _tracker.Untrack(entry.Hwnd);
        ComRelease.Release(entry.TempDesktop);
        return true;
    }

    public void HandleWindowDestroyed(IntPtr hwnd)
    {
        if (!_inFlight.Add(hwnd)) return;
        try
        {
            var entry = _tracker.Get(hwnd);
            if (entry == null) return;
            // Closing a background window must never pull the user off their desktop.
            if (_vds.GetCurrentDesktopId() == entry.TempDesktopId)
            {
                var original = _vds.FindDesktop(entry.OriginalDesktopId);
                try { if (original != null) _vds.SwitchToDesktop(original); }
                finally { if (original != null) ComRelease.Release(original); }
            }
            FinishDesktop(entry, hwnd);
        }
        finally { _inFlight.Remove(hwnd); }
    }

    public void RestoreAll()
    {
        foreach (var entry in _tracker.GetAll())
        {
            try { Restore(entry.Hwnd); }
            catch (Exception ex) { Trace.WriteLine($"FullScreenManager: Restore failed for {entry.Hwnd}: {ex.Message}"); }
        }
    }

    public void CleanupStaleEntries()
    {
        foreach (var hwnd in _tracker.GetStaleHandles()) HandleWindowDestroyed(hwnd);
    }

    public void PinToggle(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            Trace.WriteLine($"FullScreenManager: hwnd {hwnd} is not valid, ignoring pin toggle.");
            return;
        }

        string? processName = null;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            processName = !string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? process.MainWindowTitle
                : process.ProcessName;
        }
        catch { }

        if (_vds.IsWindowPinned(hwnd))
        {
            if (_vds.UnpinWindow(hwnd))
                NotificationOverlay.ShowNotification("📌 Unpinned", processName ?? "", hwnd);
            else
                NotificationOverlay.ShowNotification("⚠ Unpin Failed", processName ?? "", hwnd);
        }
        else
        {
            if (_vds.PinWindow(hwnd))
                NotificationOverlay.ShowNotification("📌 Pinned to All Desktops", processName ?? "", hwnd);
            else
                NotificationOverlay.ShowNotification("⚠ Pin Failed", processName ?? "", hwnd);
        }
    }

}
