using System.Runtime.InteropServices;
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
        return 0;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
