using System.Drawing;
using System.Runtime.InteropServices;

namespace DebugCapture.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly Rectangle ToRectangle()
    {
        return Rectangle.FromLTRB(this.Left, this.Top, this.Right, this.Bottom);
    }
}
