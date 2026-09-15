using DebugCapture.Interop;
using System;
using System.Drawing;

namespace DebugCapture.Services;

internal sealed class WindowBoundsProvider : IWindowBoundsProvider
{
    public bool TryGetWindowBounds(IntPtr windowHandle, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;

        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(windowHandle, out var rect))
        {
            return false;
        }

        bounds = rect.ToRectangle();
        return bounds.Width > 0 && bounds.Height > 0;
    }
}
