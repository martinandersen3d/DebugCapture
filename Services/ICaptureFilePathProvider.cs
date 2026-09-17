using DebugCapture.Models;

namespace DebugCapture.Services;

internal interface ICaptureFilePathProvider
{
    CaptureFileSet CreateFileSet(SnapshotTrigger trigger);
}
