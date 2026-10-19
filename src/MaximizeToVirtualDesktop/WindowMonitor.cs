using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>Coalesces native events and reconciles actual window state on the UI thread.</summary>
internal sealed class WindowMonitor : IDisposable
{
    private readonly FullScreenManager _manager;
    private readonly FullScreenTracker _tracker;
    private readonly Control _syncControl;
    private readonly AppSettings _settings;
    private readonly Func<IntPtr, bool> _eligible;
    private readonly AutomaticWindowPolicy _policy = new();
    private readonly HashSet<IntPtr> _pending = [];
    private readonly List<IntPtr> _hooks = [];
    private readonly NativeMethods.WinEventProc _callback;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private bool _disposed;

    public WindowMonitor(FullScreenManager manager, FullScreenTracker tracker, Control syncControl,
        AppSettings settings, Func<IntPtr, bool>? eligible = null)
    {
        _manager = manager;
        _tracker = tracker;
        _syncControl = syncControl;
        _settings = settings;
        _eligible = eligible ?? (hwnd => WindowEligibility.IsApplicationWindow(hwnd, settings));
        _callback = OnEvent;
        _timer.Tick += (_, _) => ReconcilePending();
    }

    public void Start()
    {
        if (_disposed || _hooks.Count != 0) return;
        foreach (uint eventId in new[] { NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_DESTROY,
            NativeMethods.EVENT_SYSTEM_MOVESIZEEND })
        {
            var hook = NativeMethods.SetWinEventHook(eventId, eventId, IntPtr.Zero,
                _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            if (hook == IntPtr.Zero) { Dispose(); throw new InvalidOperationException($"Cannot monitor event {eventId:X}."); }
            _hooks.Add(hook);
        }
        _timer.Start();
        if (_settings.AutoManageMaximizedWindows && _settings.ManageExistingWindowsOnStart) ScanExistingWindows();
    }

    public void ScanExistingWindows() => NativeMethods.EnumWindows((hwnd, _) =>
    {
        if (_eligible(hwnd)) _pending.Add(hwnd);
        return true;
    }, IntPtr.Zero);

    private void OnEvent(IntPtr hook, uint eventId, IntPtr hwnd, int objectId,
        int childId, uint threadId, uint timestamp)
    {
        if (hwnd == IntPtr.Zero || objectId != NativeMethods.OBJID_WINDOW || childId != 0) return;
        bool shiftHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        Post(() =>
        {
            if (eventId == NativeMethods.EVENT_OBJECT_DESTROY)
            {
                _pending.Remove(hwnd);
                _policy.Forget(hwnd);
                if (_tracker.IsTracked(hwnd)) _manager.HandleWindowDestroyed(hwnd);
                return;
            }
            if (shiftHeld && !_tracker.IsTracked(hwnd)) _policy.Bypass(hwnd);
            _pending.Add(hwnd);
        });
    }

    private void ReconcilePending()
    {
        var windows = _pending.ToArray();
        _pending.Clear();
        foreach (var hwnd in windows)
        {
            if (!NativeMethods.IsWindow(hwnd)) { _policy.Forget(hwnd); continue; }
            var placement = NativeMethods.WINDOWPLACEMENT.Default;
            if (!NativeMethods.GetWindowPlacement(hwnd, ref placement)) continue;
            bool tracked = _tracker.IsTracked(hwnd);
            var action = _policy.Decide(hwnd, new(
                placement.showCmd == NativeMethods.SW_MAXIMIZE, tracked,
                _manager.IsBusy(hwnd), !tracked && _eligible(hwnd) && _manager.CanAutomaticallyManage(hwnd)),
                _settings.AutoManageMaximizedWindows);
            if (action == AutomaticWindowAction.Enter) _manager.MaximizeToDesktop(hwnd);
            else if (action == AutomaticWindowAction.Restore) _manager.Restore(hwnd, preserveWindowState: true);
        }
    }

    private void Post(Action action)
    {
        if (_disposed || _syncControl.IsDisposed || !_syncControl.IsHandleCreated) return;
        try { _syncControl.BeginInvoke(() => { if (!_disposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        _pending.Clear();
        _policy.Clear();
    }
}
