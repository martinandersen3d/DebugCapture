using System;

namespace DebugCapture.Services;

internal sealed class CaptureNotificationService : ICaptureNotificationService
{
    public static CaptureNotificationService Shared { get; } = new();

    public event EventHandler<CaptureCompletedEventArgs>? CaptureCompleted;

    public void NotifyCaptureCompleted(string filePath)
    {
        this.CaptureCompleted?.Invoke(this, new CaptureCompletedEventArgs(filePath));
    }
}
