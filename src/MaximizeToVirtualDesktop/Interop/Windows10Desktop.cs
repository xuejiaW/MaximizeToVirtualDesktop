// Windows 10 declarations adapted from MScholtes/VirtualDesktop (MIT).
// https://github.com/MScholtes/VirtualDesktop/blob/master/VirtualDesktop.cs
using System.Runtime.InteropServices;

namespace MaximizeToVirtualDesktop.Interop;

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4")]
internal interface IVirtualDesktop10
{
    bool IsViewVisible(IApplicationView view);
    Guid GetId();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6")]
internal interface IVirtualDesktopManagerInternal10
{
    int GetCount();
    void MoveViewToDesktop(IApplicationView view, IVirtualDesktop10 desktop);
    bool CanViewMoveDesktops(IApplicationView view);
    IVirtualDesktop10 GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    [PreserveSig]
    int GetAdjacentDesktop(IVirtualDesktop10 from, int direction, out IVirtualDesktop10 desktop);
    void SwitchDesktop(IVirtualDesktop10 desktop);
    IVirtualDesktop10 CreateDesktop();
    void RemoveDesktop(IVirtualDesktop10 desktop, IVirtualDesktop10 fallback);
    IVirtualDesktop10 FindDesktop(ref Guid desktopId);
}

// Keep version-specific native interfaces inside the adapter. The rest of the
// application uses only the shared desktop identity and lifetime operations.
internal sealed class Windows10Desktop(IVirtualDesktop10 native) : IVirtualDesktop, IDisposable
{
    internal IVirtualDesktop10 Native { get; } = native;
    private bool _disposed;
    public bool IsViewVisible(IApplicationView view) => Native.IsViewVisible(view);
    public Guid GetId() => Native.GetId();
    public IntPtr GetName() => throw new NotSupportedException();
    public IntPtr GetWallpaperPath() => throw new NotSupportedException();
    public bool IsRemote() => false;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Marshal.ReleaseComObject(Native);
    }
}
