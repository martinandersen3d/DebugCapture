namespace DebugCapture.Services;

internal interface IFeatureFlagService
{
    bool IsEnabled(CaptureFeature feature);

    void SetEnabled(CaptureFeature feature, bool isEnabled);
}
