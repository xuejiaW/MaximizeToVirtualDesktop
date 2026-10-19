using System.Diagnostics;
using System.Runtime.InteropServices;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Low-level mouse hook that detects Shift+Click on a window's maximize button.
/// Works wherever Windows 11 Snap Layouts works (apps that return HTMAXBUTTON from WM_NCHITTEST).
/// </summary>
internal sealed class MaximizeButtonHook : IDisposable
{
    private readonly FullScreenManager _manager;
    private readonly Control _syncControl;
    private readonly AppSettings _settings;
    private IntPtr _hookHandle;
    private bool _disposed;

    // Must be stored as a field to prevent GC collection
    private readonly NativeMethods.LowLevelHookProc _hookProc;

    public MaximizeButtonHook(FullScreenManager manager, Control syncControl, AppSettings settings)
    {
        _manager = manager;
        _syncControl = syncControl;
        _settings = settings;
        _hookProc = HookCallback;
    }

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero) return;

        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _hookProc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            Trace.WriteLine("MaximizeButtonHook: Failed to install mouse hook.");
        }
        else
        {
            Trace.WriteLine("MaximizeButtonHook: Installed.");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Automatic mode handles actual state changes, avoiding a second trigger
        // from intercepting the same click before the window maximizes.
        if (_settings.AutoManageMaximizedWindows)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        if (nCode >= NativeMethods.HC_ACTION && wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN)
        {
            bool shiftHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
            // Normal mode:   Shift+Click  → virtual desktop
            // Inverted mode: plain Click  → virtual desktop; Shift+Click → normal maximize
            bool triggerVirtualDesktop = _settings.InvertShiftClick ? !shiftHeld : shiftHeld;

            if (triggerVirtualDesktop)
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var hwnd = NativeMethods.WindowFromPoint(hookStruct.pt);

                if (hwnd != IntPtr.Zero && IsClickOnMaximizeButton(hwnd, hookStruct.pt))
                {
                    // Find the top-level window (the click target might be a child)
                    var topLevel = GetTopLevelWindow(hwnd);
                    if (topLevel != IntPtr.Zero)
                    {
                        // Post to UI thread — COM calls cannot be made inside an input-synchronous hook
                        try
                        {
                            if (!_syncControl.IsDisposed && _syncControl.IsHandleCreated)
                            {
                                _syncControl.BeginInvoke(() => _manager.Toggle(topLevel));
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            // App is shutting down
                        }

                        // Suppress the click
                        return (IntPtr)1;
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static bool IsClickOnMaximizeButton(IntPtr hwnd, NativeMethods.POINT pt)
    {
        try
        {
            // Use SendMessageTimeout to avoid blocking if the target window is hung.
            // A hung window would cause Windows to silently remove our LL hook.
            IntPtr lParam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
            IntPtr result = NativeMethods.SendMessageTimeout(
                hwnd, NativeMethods.WM_NCHITTEST, IntPtr.Zero, lParam,
                NativeMethods.SMTO_ABORTIFHUNG, 100, out IntPtr hitResult);
            return result != IntPtr.Zero && hitResult == (IntPtr)NativeMethods.HTMAXBUTTON;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr GetTopLevelWindow(IntPtr hwnd)
    {
        // Walk up the parent chain to find the top-level window
        IntPtr current = hwnd;
        IntPtr parent;
        while ((parent = GetParent(current)) != IntPtr.Zero)
        {
            current = parent;
        }
        return current;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            Trace.WriteLine("MaximizeButtonHook: Uninstalled.");
        }
    }
}
