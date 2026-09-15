using System;

namespace DebugCapture.Services;

internal sealed class FeatureFlagService : IFeatureFlagService
{
    public bool IsBreakpointCaptureEnabled { get; private set; } = true;

    public bool IsStepCaptureEnabled { get; private set; } = true;

    public bool IsEnabled(CaptureFeature feature)
    {
        return feature switch
        {
            CaptureFeature.Breakpoint => this.IsBreakpointCaptureEnabled,
            CaptureFeature.Step => this.IsStepCaptureEnabled,
            _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null),
        };
    }

    public void SetEnabled(CaptureFeature feature, bool isEnabled)
    {
        switch (feature)
        {
            case CaptureFeature.Breakpoint:
                this.IsBreakpointCaptureEnabled = isEnabled;
                break;
            case CaptureFeature.Step:
                this.IsStepCaptureEnabled = isEnabled;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(feature), feature, null);
        }
    }
}
