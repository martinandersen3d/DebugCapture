using System;

namespace DebugCapture.Services;

internal sealed class CaptureFileSet
{
    public CaptureFileSet(ScreenshotCaptureTrigger trigger, DateTime timestamp, string imageFilePath, string variablesFilePath)
    {
        this.Trigger = trigger;
        this.Timestamp = timestamp;
        this.ImageFilePath = imageFilePath ?? throw new ArgumentNullException(nameof(imageFilePath));
        this.VariablesFilePath = variablesFilePath ?? throw new ArgumentNullException(nameof(variablesFilePath));
    }

    public ScreenshotCaptureTrigger Trigger { get; }

    public DateTime Timestamp { get; }

    public string ImageFilePath { get; }

    public string VariablesFilePath { get; }
}
