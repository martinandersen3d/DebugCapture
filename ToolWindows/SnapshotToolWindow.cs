using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace DebugCapture.ToolWindows;

[Guid("4379d56b-9991-4721-a777-eb232bb34d35")]
internal sealed class SnapshotToolWindow : ToolWindowPane
{
    public SnapshotToolWindow() : base(null)
    {
        this.Caption = "Debug Capture Snapshots";
        this.Content = new SnapshotToolWindowControl();
    }
}
