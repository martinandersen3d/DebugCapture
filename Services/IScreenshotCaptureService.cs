using System;
using System.Threading.Tasks;

namespace DebugCapture.Services;

internal interface IScreenshotCaptureService
{
    Task CaptureAsync(IntPtr windowHandle, ScreenshotCaptureTrigger trigger);
}
