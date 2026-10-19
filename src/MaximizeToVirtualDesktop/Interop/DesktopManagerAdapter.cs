using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MaximizeToVirtualDesktop.Interop;

/// <summary>
/// Abstraction over version-specific IVirtualDesktopManagerInternal COM interfaces.
/// Exposes only the methods we use, with correct vtable mapping for each Windows build.
/// </summary>
internal abstract class DesktopManagerAdapter : IDisposable
{
    public abstract int GetCount();
    public abstract IVirtualDesktop GetCurrentDesktop();
    public abstract IVirtualDesktop CreateDesktop();
    public abstract void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
    public abstract int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    public abstract void SwitchDesktopWithAnimation(IVirtualDesktop desktop);
    public abstract void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    public abstract IVirtualDesktop FindDesktop(ref Guid desktopId);
    public abstract void SetDesktopName(IVirtualDesktop desktop, IntPtr nameHString);
    public abstract void Dispose();

    /// <summary>
    /// Creates the appropriate adapter for the current Windows build.
    /// Tries build-specific adapter first, falls back to the other if smoke test fails.
    /// </summary>
    public static DesktopManagerAdapter Create(object comObject, int buildNumber)
    {
        if (buildNumber < 22000)
        {
            var windows10 = new AdapterWindows10((IVirtualDesktopManagerInternal10)comObject);
            if (windows10.SmokeTest()) return windows10;
            windows10.Dispose();
            throw new InvalidOperationException("Windows 10 virtual desktop interface validation failed.");
        }
        // Try build-appropriate adapter first
        var primary = buildNumber >= 26100
            ? TryCreate24H2(comObject)
            : TryCreatePre24H2(comObject);

        if (primary != null && primary.SmokeTest())
        {
            Trace.WriteLine($"DesktopManagerAdapter: Using {(buildNumber >= 26100 ? "24H2" : "pre-24H2")} adapter (build {buildNumber}).");
            return primary;
        }
        primary?.Dispose();

        // Try the other adapter as fallback
        Trace.WriteLine("DesktopManagerAdapter: Primary adapter failed smoke test, trying fallback...");
        var fallback = buildNumber >= 26100
            ? TryCreatePre24H2(comObject)
            : TryCreate24H2(comObject);

        if (fallback != null && fallback.SmokeTest())
        {
            Trace.WriteLine($"DesktopManagerAdapter: Fallback {(buildNumber >= 26100 ? "pre-24H2" : "24H2")} adapter succeeded.");
            return fallback;
        }
        fallback?.Dispose();

        throw new InvalidOperationException("No compatible Virtual Desktop COM adapter found for this Windows build.");
    }

    private static DesktopManagerAdapter? TryCreate24H2(object comObject)
    {
        try { return new Adapter24H2((IVirtualDesktopManagerInternal24H2)comObject); }
        catch { return null; }
    }

    private static DesktopManagerAdapter? TryCreatePre24H2(object comObject)
    {
        try { return new AdapterPre24H2((IVirtualDesktopManagerInternalPre24H2)comObject); }
        catch { return null; }
    }

    /// <summary>
    /// Verify the adapter works by calling GetCount (same slot on both versions)
    /// and FindDesktop (divergent slots — will crash on wrong adapter).
    /// </summary>
    private bool SmokeTest()
    {
        IVirtualDesktop? current = null;
        IVirtualDesktop? found = null;
        try
        {
            var count = GetCount();
            if (count < 1 || count > 200) return false;

            current = GetCurrentDesktop();
            var id = current.GetId();
            found = FindDesktop(ref id);
            return found != null;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (found != null) ComRelease.Release(found);
            if (current != null) ComRelease.Release(current);
        }
    }

    // --- 24H2 adapter (build 26100+) ---

    private sealed class AdapterWindows10(IVirtualDesktopManagerInternal10 com) : DesktopManagerAdapter
    {
        public override int GetCount() => com.GetCount();
        public override IVirtualDesktop GetCurrentDesktop() => new Windows10Desktop(com.GetCurrentDesktop());
        public override IVirtualDesktop CreateDesktop() => new Windows10Desktop(com.CreateDesktop());
        private static IVirtualDesktop10 Native(IVirtualDesktop desktop) => ((Windows10Desktop)desktop).Native;
        public override void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop)
            => com.MoveViewToDesktop(view, Native(desktop));
        public override int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop)
        {
            int hr = com.GetAdjacentDesktop(Native(from), direction, out var adjacent);
            desktop = hr == 0 ? new Windows10Desktop(adjacent) : null!;
            return hr;
        }
        public override void SwitchDesktopWithAnimation(IVirtualDesktop desktop) => com.SwitchDesktop(Native(desktop));
        public override void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback)
            => com.RemoveDesktop(Native(desktop), Native(fallback));
        public override IVirtualDesktop FindDesktop(ref Guid desktopId) => new Windows10Desktop(com.FindDesktop(ref desktopId));
        // Naming is optional; desktop identity and recovery do not depend on it.
        public override void SetDesktopName(IVirtualDesktop desktop, IntPtr nameHString) { }
        public override void Dispose() => ComRelease.Release(com);
    }

    private sealed class Adapter24H2 : DesktopManagerAdapter
    {
        private IVirtualDesktopManagerInternal24H2? _com;
        public Adapter24H2(IVirtualDesktopManagerInternal24H2 com) => _com = com;

        public override int GetCount() => _com!.GetCount();
        public override IVirtualDesktop GetCurrentDesktop() => _com!.GetCurrentDesktop();
        public override IVirtualDesktop CreateDesktop() => _com!.CreateDesktop();
        public override void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop)
            => _com!.MoveViewToDesktop(view, desktop);
        public override int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop)
            => _com!.GetAdjacentDesktop(from, direction, out desktop);
        public override void SwitchDesktopWithAnimation(IVirtualDesktop desktop)
            => _com!.SwitchDesktopWithAnimation(desktop);
        public override void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback)
            => _com!.RemoveDesktop(desktop, fallback);
        public override IVirtualDesktop FindDesktop(ref Guid desktopId)
            => _com!.FindDesktop(ref desktopId);
        public override void SetDesktopName(IVirtualDesktop desktop, IntPtr nameHString)
            => _com!.SetDesktopName(desktop, nameHString);

        public override void Dispose()
        {
            if (_com != null) { ComRelease.Release(_com); _com = null; }
        }
    }

    // --- Pre-24H2 adapter (builds 22000-26099) ---

    private sealed class AdapterPre24H2 : DesktopManagerAdapter
    {
        private IVirtualDesktopManagerInternalPre24H2? _com;
        public AdapterPre24H2(IVirtualDesktopManagerInternalPre24H2 com) => _com = com;

        public override int GetCount() => _com!.GetCount();
        public override IVirtualDesktop GetCurrentDesktop() => _com!.GetCurrentDesktop();
        public override IVirtualDesktop CreateDesktop() => _com!.CreateDesktop();
        public override void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop)
            => _com!.MoveViewToDesktop(view, desktop);
        public override int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop)
            => _com!.GetAdjacentDesktop(from, direction, out desktop);
        public override void SwitchDesktopWithAnimation(IVirtualDesktop desktop)
            => _com!.SwitchDesktopWithAnimation(desktop);
        public override void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback)
            => _com!.RemoveDesktop(desktop, fallback);
        public override IVirtualDesktop FindDesktop(ref Guid desktopId)
            => _com!.FindDesktop(ref desktopId);
        public override void SetDesktopName(IVirtualDesktop desktop, IntPtr nameHString)
            => _com!.SetDesktopName(desktop, nameHString);

        public override void Dispose()
        {
            if (_com != null) { ComRelease.Release(_com); _com = null; }
        }
    }
}
