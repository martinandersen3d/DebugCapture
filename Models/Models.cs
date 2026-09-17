using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DebugCapture.Models;

public class Snapshot
{
    public SnapshotTrigger Trigger { get; set; }

    public DateTime Timestamp { get; set; }

    public string Filename { get; set; }

    public string Filepath { get; set; }

    public int LineNumber { get; set; }

    /// <summary>
    /// The text of the line where the snapshot was taken.
    /// </summary>
    public string LineText { get; set; }

    public List<SnapshotProperty> Locals { get; set; } = new();

    public List<SnapshotProperty> Autos { get; set; } = new();

    public List<SnapshotProperty> Watch1 { get; set; } = new();

    public List<SnapshotProperty> Watch2 { get; set; } = new();

    public List<SnapshotProperty> Watch3 { get; set; } = new();

    public List<SnapshotProperty> Watch4 { get; set; } = new();

    public SnapshotException Exception { get; set; }

    public List<SnapshotCallStackFrame> CallStack { get; set; } = new();
}

public class SnapshotProperty
{
    public string Name { get; set; }

    public string Value { get; set; }

    public string Type { get; set; }
}

public class SnapshotException
{
    public string TypeName { get; set; }

    public string Message { get; set; }

    public string Source { get; set; }

    public string TargetSite { get; set; }

    public string HResult { get; set; }

    public string StackTrace { get; set; }

    public SnapshotException InnerException { get; set; }

    public List<SnapshotProperty> Members { get; set; } = new();
}

public class SnapshotCallStackFrame
{
    public string FunctionName { get; set; }

    public string Module { get; set; }
}

public enum SnapshotTrigger
{
    Breakpoint,
    Step,
    StepIn,
    StepOver,
    Exception,
}

public static class DebugTriggerExtensions
{
    public static string GetFileSuffix(this SnapshotTrigger trigger)
    {
        return trigger switch
        {
            SnapshotTrigger.Breakpoint => "BREAKPOINT",
            SnapshotTrigger.Step => "STEP",
            SnapshotTrigger.StepIn => "STEP-IN",
            SnapshotTrigger.StepOver => "STEP-OVER",
            SnapshotTrigger.Exception => "EXCEPTION",
            _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null),
        };
    }
}