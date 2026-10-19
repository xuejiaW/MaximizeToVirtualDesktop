using System.Runtime.InteropServices;
using System.Diagnostics;
using MaximizeToVirtualDesktop;
using MaximizeToVirtualDesktop.Interop;

internal static class Program
{
    [STAThread]
    static int Main()
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Out));
        System.Diagnostics.Trace.AutoFlush = true;
        Application.EnableVisualStyles();
        using var desktops = new VirtualDesktopService();
        if (!desktops.Initialize(Environment.OSVersion.Version.Build))
            throw new InvalidOperationException("Desktop interface initialization failed.");
        int before = desktops.GetDesktopCount();
        var originalId = desktops.GetCurrentDesktopId();
        IVirtualDesktop? created = null;
        try
        {
            (created, var id) = desktops.CreateDesktop();
            Require(created != null && id != null, "create desktop");
            Require(desktops.GetDesktopCount() == before + 1, "desktop count after create");
            var found = desktops.FindDesktop(id!.Value);
            Require(found != null && found.GetId() == id, "find created desktop");
            if (found != null) ComRelease.Release(found);
        }
        finally
        {
            if (created != null)
            {
                Require(desktops.RemoveDesktop(created), "remove owned test desktop");
                ComRelease.Release(created);
            }
        }
        Require(desktops.GetCurrentDesktopId() == originalId, "starting desktop preserved");
        Require(desktops.GetDesktopCount() == before, "desktop count restored");
        Console.WriteLine("PASS: desktop interface smoke test");
        RunAutomaticLifecycle(desktops);
        return 0;
    }

    private static void RunAutomaticLifecycle(VirtualDesktopService desktops)
    {
        int before = desktops.GetDesktopCount();
        Guid original = desktops.GetCurrentDesktopId()!.Value;
        var ownedDesktopIds = new HashSet<Guid>();
        var settings = new AppSettings { AutoManageMaximizedWindows = true, ShowSwitchPopup = false };
        var tracker = new FullScreenTracker(persist: false);
        var manager = new FullScreenManager(desktops, tracker, settings);
        using var controller = new Control();
        _ = controller.Handle;
        using var window = new Form { Text = "MVD smoke window", Size = new(640, 400) };
        using var startedMaximized = new Form { Text = "MVD smoke initially maximized", WindowState = FormWindowState.Maximized };
        using var retainedWindow = new Form { Text = "MVD smoke retained desktop", Size = new(400, 300) };
        var ownedWindows = new HashSet<IntPtr> { window.Handle, startedMaximized.Handle, retainedWindow.Handle };
        using var monitor = new WindowMonitor(manager, tracker, controller, settings,
            hwnd => ownedWindows.Contains(hwnd) && WindowEligibility.IsApplicationWindow(hwnd, settings, allowOwnProcess: true));
        monitor.Start();
        try
        {
            window.Show();
            Pump(350);
            Require(tracker.Count == 0, "normal window is not adopted");
            window.WindowState = FormWindowState.Maximized;
            WaitUntil(() => tracker.IsTracked(window.Handle), "ordinary maximize automatically enters desktop");
            var entry = tracker.Get(window.Handle)!;
            ownedDesktopIds.Add(entry.TempDesktopId);
            Require(desktops.GetDesktopIdForWindow(window.Handle) == entry.TempDesktopId, "window moved to its desktop");
            manager.MaximizeToDesktop(window.Handle);
            manager.MaximizeToDesktop(window.Handle);
            Pump(400);
            Require(desktops.GetDesktopCount() == before + 1 && tracker.Count == 1, "repeated maximize is idempotent");

            window.WindowState = FormWindowState.Normal;
            WaitUntil(() => tracker.Count == 0, "restore removes temporary desktop");
            Require(desktops.GetDesktopCount() == before && desktops.GetDesktopIdForWindow(window.Handle) == original,
                "restored window returns to original desktop");

            window.WindowState = FormWindowState.Maximized;
            WaitUntil(() => tracker.IsTracked(window.Handle), "second maximize enters again");
            ownedDesktopIds.Add(tracker.Get(window.Handle)!.TempDesktopId);
            window.WindowState = FormWindowState.Minimized;
            WaitUntil(() => tracker.Count == 0, "minimize releases temporary desktop");
            Require(window.WindowState == FormWindowState.Minimized, "minimized window stays minimized");

            startedMaximized.Show();
            WaitUntil(() => tracker.IsTracked(startedMaximized.Handle), "window launched maximized is adopted");
            var backgroundEntry = tracker.Get(startedMaximized.Handle)!;
            ownedDesktopIds.Add(backgroundEntry.TempDesktopId);
            var originalDesktop = desktops.FindDesktop(original)!;
            try { Require(desktops.SwitchToDesktop(originalDesktop), "return to original desktop before background close"); }
            finally { ComRelease.Release(originalDesktop); }
            Pump(300);
            startedMaximized.Close();
            WaitUntil(() => tracker.Count == 0, "closed background window is cleaned up");
            Require(desktops.GetCurrentDesktopId() == original, "background close does not steal desktop focus");

            window.WindowState = FormWindowState.Maximized;
            WaitUntil(() => tracker.IsTracked(window.Handle), "create desktop for occupancy test");
            var occupiedId = tracker.Get(window.Handle)!.TempDesktopId;
            ownedDesktopIds.Add(occupiedId);
            retainedWindow.Show();
            Pump(350);
            window.Close();
            WaitUntil(() => tracker.Count == 0, "owner close untracks window");
            Require(desktops.FindDesktop(occupiedId) is { } retained && ReleaseAndTrue(retained),
                "desktop containing another window is retained");
            Require(desktops.GetDesktopIdForWindow(retainedWindow.Handle) == occupiedId,
                "unrelated window is not relocated by cleanup");
        }
        finally
        {
            monitor.Dispose();
            manager.RestoreAll();
            window.Close();
            startedMaximized.Close();
            retainedWindow.Close();
            Pump(100);
            var originalDesktop = desktops.FindDesktop(original);
            try { if (originalDesktop != null) desktops.SwitchToDesktop(originalDesktop); }
            finally { if (originalDesktop != null) ComRelease.Release(originalDesktop); }
            foreach (var id in ownedDesktopIds)
            {
                var desktop = desktops.FindDesktop(id);
                if (desktop == null) continue;
                try { Require(desktops.RemoveDesktop(desktop), "remove remaining owned test desktop"); }
                finally { ComRelease.Release(desktop); }
            }
        }
        Require(desktops.GetDesktopCount() == before, "all test desktops cleaned up");
        Require(desktops.GetCurrentDesktopId() == original, "original desktop restored after lifecycle tests");
        Console.WriteLine("PASS: automatic window lifecycle smoke tests");
    }

    private static bool ReleaseAndTrue(IVirtualDesktop desktop) { ComRelease.Release(desktop); return true; }

    private static void WaitUntil(Func<bool> predicate, string name)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate() && deadline.Elapsed < TimeSpan.FromSeconds(8)) Pump(25);
        Require(predicate(), name);
    }

    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
