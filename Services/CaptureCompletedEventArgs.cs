using System;

namespace DebugCapture.Services;

internal sealed class CaptureCompletedEventArgs : EventArgs
{
    public CaptureCompletedEventArgs(string filePath)
    {
        this.FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public string FilePath { get; }
}
