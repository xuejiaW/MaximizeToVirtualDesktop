using System.Runtime.InteropServices;

namespace MaximizeToVirtualDesktop.Interop;

internal static class ComRelease
{
    public static void Release(object value)
    {
        if (value is Windows10Desktop desktop) desktop.Dispose();
        else if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
