using System;
using DebugCapture.Models;

namespace DebugCapture.Services;

internal sealed class CaptureFileSet
{
    public CaptureFileSet(SnapshotTrigger trigger, DateTimeOffset timestamp, string imageFilePath, string variablesFilePath)
    {
        this.Trigger = trigger;
        this.Timestamp = timestamp;
        this.ImageFilePath = imageFilePath ?? throw new ArgumentNullException(nameof(imageFilePath));
        this.VariablesFilePath = variablesFilePath ?? throw new ArgumentNullException(nameof(variablesFilePath));
    }

    public SnapshotTrigger Trigger { get; }

    public DateTimeOffset Timestamp { get; }

    public string ImageFilePath { get; }

    public string VariablesFilePath { get; }
}
