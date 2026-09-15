namespace DebugCapture.Services;

internal interface ICaptureFilePathProvider
{
    CaptureFileSet CreateFileSet(ScreenshotCaptureTrigger trigger);
}
