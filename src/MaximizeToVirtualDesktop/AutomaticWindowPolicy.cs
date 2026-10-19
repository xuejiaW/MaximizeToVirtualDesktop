namespace MaximizeToVirtualDesktop;

internal enum AutomaticWindowAction { None, Enter, Restore }
internal readonly record struct WindowState(bool Maximized, bool Tracked, bool Busy, bool Eligible);

/// <summary>Remembers explicit Shift bypasses until the window leaves maximized state.</summary>
internal sealed class AutomaticWindowPolicy
{
    private readonly HashSet<IntPtr> _bypassed = [];
    public void Bypass(IntPtr hwnd) => _bypassed.Add(hwnd);
    public void Forget(IntPtr hwnd) => _bypassed.Remove(hwnd);
    public void Clear() => _bypassed.Clear();
    public AutomaticWindowAction Decide(IntPtr hwnd, WindowState state, bool automaticEnabled)
    {
        if (state.Busy) return AutomaticWindowAction.None;
        if (!state.Maximized) _bypassed.Remove(hwnd);
        if (state.Tracked)
            return state.Maximized ? AutomaticWindowAction.None : AutomaticWindowAction.Restore;
        return automaticEnabled && state.Maximized && state.Eligible && !_bypassed.Contains(hwnd)
            ? AutomaticWindowAction.Enter : AutomaticWindowAction.None;
    }
}
