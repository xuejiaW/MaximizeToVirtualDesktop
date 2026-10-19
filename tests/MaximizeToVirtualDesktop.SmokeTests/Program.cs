using System.Runtime.InteropServices;
using System.Diagnostics;
using MaximizeToVirtualDesktop;
using MaximizeToVirtualDesktop.Interop;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--child-window")) return RunChildWindow();
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
        RunNavigation(desktops);
        RunCrossProcess(desktops);
        RenderSettings();
        return 0;
    }

    private static int RunChildWindow()
    {
        Application.EnableVisualStyles();
        using var form = new Form { Text = "MVD external smoke window", Size = new(640, 400) };
        Console.WriteLine(form.Handle.ToInt64());
        Console.Out.Flush();
        _ = Task.Run(() =>
        {
            string? command;
            while ((command = Console.ReadLine()) != null)
            {
                string requested = command;
                try
                {
                    form.BeginInvoke(() =>
                    {
                        if (requested == "maximize") form.WindowState = FormWindowState.Maximized;
                        else if (requested == "restore") form.WindowState = FormWindowState.Normal;
                        else if (requested == "close") form.Close();
                    });
                }
                catch (InvalidOperationException) { break; }
                if (requested == "close") break;
            }
        });
        Application.Run(form);
        return 0;
    }

    private static void RunCrossProcess(VirtualDesktopService desktops)
    {
        int before = desktops.GetDesktopCount();
        Guid originalId = desktops.GetCurrentDesktopId()!.Value;
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--child-window")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            CreateNoWindow = true
        })!;
        var desktopIds = new HashSet<Guid>();
        var settings = new AppSettings { AutoManageMaximizedWindows = true, ShowSwitchPopup = false };
        var tracker = new FullScreenTracker(persist: false);
        var manager = new FullScreenManager(desktops, tracker, settings);
        using var controller = new Control();
        _ = controller.Handle;
        WindowMonitor? monitor = null;
        try
        {
            string? handleText = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var hwnd = new IntPtr(long.Parse(handleText!));
            monitor = new WindowMonitor(manager, tracker, controller, settings,
                candidate => candidate == hwnd && WindowEligibility.IsApplicationWindow(candidate, settings));
            monitor.Start();
            child.StandardInput.WriteLine("maximize");
            child.StandardInput.Flush();
            WaitUntil(() => tracker.IsTracked(hwnd), "cross-process maximize is adopted");
            var entry = tracker.Get(hwnd)!;
            desktopIds.Add(entry.TempDesktopId);
            Require(desktops.GetDesktopIdForWindow(hwnd) == entry.TempDesktopId, "cross-process window moves to its own desktop");
            child.StandardInput.WriteLine("restore");
            child.StandardInput.Flush();
            WaitUntil(() => tracker.Count == 0, "cross-process restore cleans desktop");
            Require(desktops.GetDesktopIdForWindow(hwnd) == originalId, "cross-process window returns to original desktop");
            child.StandardInput.WriteLine("maximize");
            child.StandardInput.Flush();
            WaitUntil(() => tracker.IsTracked(hwnd), "cross-process window can enter again");
            desktopIds.Add(tracker.Get(hwnd)!.TempDesktopId);
            child.StandardInput.WriteLine("close");
            child.StandardInput.Flush();
            WaitUntil(() => child.HasExited && tracker.Count == 0, "cross-process exit cleans desktop");
        }
        finally
        {
            monitor?.Dispose();
            manager.RestoreAll();
            if (!child.HasExited)
            {
                child.StandardInput.WriteLine("close");
                child.StandardInput.Flush();
                if (!child.WaitForExit(3000)) { child.Kill(); child.WaitForExit(); }
            }
            var original = desktops.FindDesktop(originalId);
            try { if (original != null) desktops.SwitchToDesktop(original); }
            finally { if (original != null) ComRelease.Release(original); }
            foreach (var id in desktopIds)
            {
                var desktop = desktops.FindDesktop(id);
                if (desktop != null) { try { desktops.RemoveDesktop(desktop); } finally { ComRelease.Release(desktop); } }
            }
        }
        Require(desktops.GetDesktopCount() == before && desktops.GetCurrentDesktopId() == originalId,
            "cross-process tests restore desktop state");
    }

    private static void RunNavigation(VirtualDesktopService desktops)
    {
        int before = desktops.GetDesktopCount();
        Guid originalId = desktops.GetCurrentDesktopId()!.Value;
        var (left, leftId) = desktops.CreateDesktop();
        var (right, rightId) = desktops.CreateDesktop();
        using var navigation = new DesktopNavigationHotkeys(goRight => desktops.SwitchAdjacentDesktop(goRight));
        try
        {
            Require(left != null && right != null, "create navigation test desktops");
            Require(navigation.Configure(true).Count == 0, "register Ctrl+[ and Ctrl+] globally");
            desktops.SwitchToDesktop(left!);
            NativeMethods.PostMessage(navigation.Handle, NativeMethods.WM_HOTKEY, (IntPtr)DesktopNavigationHotkeys.NextId, IntPtr.Zero);
            WaitUntil(() => desktops.GetCurrentDesktopId() == rightId, "next hotkey message switches right");
            NativeMethods.PostMessage(navigation.Handle, NativeMethods.WM_HOTKEY, (IntPtr)DesktopNavigationHotkeys.PreviousId, IntPtr.Zero);
            WaitUntil(() => desktops.GetCurrentDesktopId() == leftId, "previous hotkey message switches left");
            desktops.SwitchToDesktop(right!);
            Require(!desktops.SwitchAdjacentDesktop(true) && desktops.GetCurrentDesktopId() == rightId,
                "navigation stops at the last desktop");
            navigation.Configure(false);
            desktops.SwitchToDesktop(left!);
            NativeMethods.PostMessage(navigation.Handle, NativeMethods.WM_HOTKEY, (IntPtr)DesktopNavigationHotkeys.NextId, IntPtr.Zero);
            Pump(250);
            Require(desktops.GetCurrentDesktopId() == leftId, "disabled navigation ignores queued hotkey messages");
        }
        finally
        {
            navigation.Dispose();
            var original = desktops.FindDesktop(originalId);
            try { if (original != null) desktops.SwitchToDesktop(original); }
            finally { if (original != null) ComRelease.Release(original); }
            if (right != null) { try { Require(desktops.RemoveDesktop(right), "remove right navigation desktop"); } finally { ComRelease.Release(right); } }
            if (left != null) { try { Require(desktops.RemoveDesktop(left), "remove left navigation desktop"); } finally { ComRelease.Release(left); } }
        }
        Require(desktops.GetDesktopCount() == before && desktops.GetCurrentDesktopId() == originalId,
            "navigation test restores desktop state");
    }

    private static void RenderSettings()
    {
        using var settings = new SettingsDialog(new AppSettings());
        settings.Show();
        Pump(100);
        using var bitmap = new Bitmap(settings.Width, settings.Height);
        settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, settings.Size));
        Directory.CreateDirectory("artifacts");
        bitmap.Save("artifacts/settings.png", System.Drawing.Imaging.ImageFormat.Png);
        settings.Close();
        Console.WriteLine("PASS: settings dialog rendered to artifacts/settings.png");
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
