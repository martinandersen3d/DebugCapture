using System;
using System.Drawing;

namespace DebugCapture.Services;

internal interface IWindowBoundsProvider
{
    bool TryGetWindowBounds(IntPtr windowHandle, out Rectangle bounds);
}
