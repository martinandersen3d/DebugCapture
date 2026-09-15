using System;

namespace DebugCapture.Services;

internal enum ScreenshotCaptureTrigger
{
    Breakpoint,
    Step,
    StepIn,
    StepOver,
    Exception,
}

internal static class ScreenshotCaptureTriggerExtensions
{
    public static string GetFileSuffix(this ScreenshotCaptureTrigger trigger)
    {
        return trigger switch
        {
            ScreenshotCaptureTrigger.Breakpoint => "BREAKPOINT",
            ScreenshotCaptureTrigger.Step => "STEP",
            ScreenshotCaptureTrigger.StepIn => "STEP-IN",
            ScreenshotCaptureTrigger.StepOver => "STEP-OVER",
            ScreenshotCaptureTrigger.Exception => "EXCEPTION",
            _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null),
        };
    }
}
