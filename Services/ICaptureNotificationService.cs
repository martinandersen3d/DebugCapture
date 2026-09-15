using System;

namespace DebugCapture.Services;

internal interface ICaptureNotificationService
{
    event EventHandler<CaptureCompletedEventArgs>? CaptureCompleted;

    void NotifyCaptureCompleted(string filePath);
}
