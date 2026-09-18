using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace DebugCapture.ToolWindows;

[Guid("4379d56b-9991-4721-a777-eb232bb34d35")]
internal sealed class SnapshotToolWindow : ToolWindowPane
{
    private readonly SnapshotToolWindowControl control;

    public SnapshotToolWindow() : base(null)
    {
        this.Caption = "Debug Capture Snapshots";
        this.control = new SnapshotToolWindowControl();
        this.Content = this.control;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.control.Dispose();
        }

        base.Dispose(disposing);
    }
}
