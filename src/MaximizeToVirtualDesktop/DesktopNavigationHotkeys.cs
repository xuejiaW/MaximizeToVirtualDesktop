using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>Owns the two global navigation shortcuts independently of application windows.</summary>
internal sealed class DesktopNavigationHotkeys : NativeWindow, IDisposable
{
    internal const int PreviousId = 1;
    internal const int NextId = 2;
    private readonly Action<bool> _navigate;
    private bool _enabled;

    public DesktopNavigationHotkeys(Action<bool> navigate)
    {
        _navigate = navigate;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // message-only window
    }

    public List<string> Configure(bool enabled)
    {
        _enabled = enabled;
        NativeMethods.UnregisterHotKey(Handle, PreviousId);
        NativeMethods.UnregisterHotKey(Handle, NextId);
        var failures = new List<string>();
        if (!enabled) return failures;
        uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(Handle, PreviousId, modifiers, NativeMethods.VK_OEM_4)) failures.Add("Ctrl+[");
        if (!NativeMethods.RegisterHotKey(Handle, NextId, modifiers, NativeMethods.VK_OEM_6)) failures.Add("Ctrl+]");
        return failures;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_HOTKEY
            && (message.WParam == (IntPtr)PreviousId || message.WParam == (IntPtr)NextId))
        {
            if (_enabled) _navigate(message.WParam == (IntPtr)NextId);
            return;
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Configure(false);
        DestroyHandle();
    }
}
