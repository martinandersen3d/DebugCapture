using DebugCapture.Interop;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DebugCapture.Services;

internal sealed class DebuggerBreakpointCaptureListener : IDisposable
{
    private const string TelemetryEventName = "DebugCapture/DebuggerCapture";
    private const string StepIntoCommandName = "Debug.StepInto";
    private const string StepOverCommandName = "Debug.StepOver";

    private readonly DTE2 dte;
    private readonly JoinableTaskFactory joinableTaskFactory;
    private readonly IScreenshotCaptureService screenshotCaptureService;
    private readonly IFeatureFlagService featureFlagService;
    private readonly IOutputWindowLogger logger;
    private DebuggerEvents? debuggerEvents;
    private CommandEvents? stepIntoCommandEvents;
    private CommandEvents? stepOverCommandEvents;
    private ScreenshotCaptureTrigger? lastStepTrigger;
    private bool disposed;

    public DebuggerBreakpointCaptureListener(
        DTE2 dte,
        JoinableTaskFactory joinableTaskFactory,
        IScreenshotCaptureService screenshotCaptureService,
        IFeatureFlagService featureFlagService,
        IOutputWindowLogger logger)
    {
        this.dte = dte ?? throw new ArgumentNullException(nameof(dte));
        this.joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        this.screenshotCaptureService = screenshotCaptureService ?? throw new ArgumentNullException(nameof(screenshotCaptureService));
        this.featureFlagService = featureFlagService ?? throw new ArgumentNullException(nameof(featureFlagService));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        this.debuggerEvents = this.dte.Events.DebuggerEvents;
        this.debuggerEvents.OnEnterBreakMode += this.OnEnterBreakMode;

        this.SubscribeToStepCommand(StepIntoCommandName, ScreenshotCaptureTrigger.StepIn, ref this.stepIntoCommandEvents);
        this.SubscribeToStepCommand(StepOverCommandName, ScreenshotCaptureTrigger.StepOver, ref this.stepOverCommandEvents);
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (this.disposed)
        {
            return;
        }

        if (this.debuggerEvents is not null)
        {
            this.debuggerEvents.OnEnterBreakMode -= this.OnEnterBreakMode;
            this.debuggerEvents = null;
        }

        if (this.stepIntoCommandEvents is not null)
        {
            this.stepIntoCommandEvents.BeforeExecute -= this.OnStepIntoBeforeExecute;
            this.stepIntoCommandEvents = null;
        }

        if (this.stepOverCommandEvents is not null)
        {
            this.stepOverCommandEvents.BeforeExecute -= this.OnStepOverBeforeExecute;
            this.stepOverCommandEvents = null;
        }

        this.disposed = true;
    }

    private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
    {
        if (!this.TryGetCaptureTrigger(reason, out var trigger))
        {
            return;
        }

        this.QueueCapture(trigger);
    }

    private void QueueCapture(ScreenshotCaptureTrigger trigger)
    {
        this.joinableTaskFactory.RunAsync(() => this.CaptureVisualStudioWindowAsync(trigger)).FileAndForget(TelemetryEventName);
    }

    private async Task CaptureVisualStudioWindowAsync(ScreenshotCaptureTrigger trigger)
    {
        try
        {
            var windowHandle = await this.GetVisualStudioWindowHandleAsync().ConfigureAwait(true);
            await this.screenshotCaptureService.CaptureAsync(windowHandle, trigger).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await this.LogSafelyAsync(exception).ConfigureAwait(false);
        }
    }

    private async Task<IntPtr> GetVisualStudioWindowHandleAsync()
    {
        await this.joinableTaskFactory.SwitchToMainThreadAsync();

        var windowHandle = this.dte.MainWindow.HWnd;
        return windowHandle != IntPtr.Zero ? windowHandle : NativeMethods.GetForegroundWindow();
    }

    private bool TryGetCaptureTrigger(dbgEventReason reason, out ScreenshotCaptureTrigger trigger)
    {
        switch (reason)
        {
            case dbgEventReason.dbgEventReasonBreakpoint when this.featureFlagService.IsEnabled(CaptureFeature.Breakpoint):
                trigger = ScreenshotCaptureTrigger.Breakpoint;
                return true;
            case dbgEventReason.dbgEventReasonStep when this.featureFlagService.IsEnabled(CaptureFeature.Step):
                trigger = this.TakeLastStepTrigger();
                return true;
            default:
                trigger = default;
                return false;
        }
    }

    private ScreenshotCaptureTrigger TakeLastStepTrigger()
    {
        var trigger = this.lastStepTrigger ?? ScreenshotCaptureTrigger.Step;
        this.lastStepTrigger = null;
        return trigger;
    }

    private void SubscribeToStepCommand(string commandName, ScreenshotCaptureTrigger trigger, ref CommandEvents? commandEvents)
    {
        var command = this.dte.Commands.Item(commandName);
        commandEvents = this.dte.Events.CommandEvents[command.Guid, command.ID];

        if (trigger == ScreenshotCaptureTrigger.StepIn)
        {
            commandEvents.BeforeExecute += this.OnStepIntoBeforeExecute;
        }
        else
        {
            commandEvents.BeforeExecute += this.OnStepOverBeforeExecute;
        }
    }

    private void OnStepIntoBeforeExecute(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
    {
        this.lastStepTrigger = ScreenshotCaptureTrigger.StepIn;
    }

    private void OnStepOverBeforeExecute(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
    {
        this.lastStepTrigger = ScreenshotCaptureTrigger.StepOver;
    }

    private async Task LogSafelyAsync(Exception exception)
    {
        try
        {
            await this.logger.LogAsync(exception).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
