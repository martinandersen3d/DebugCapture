using DebugCapture.Models;
using DebugCapture.Interop;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DebugCapture.Services;

internal sealed class DebuggerBreakpointCaptureListener : IDisposable
{
    private const string TelemetryEventName = "DebugCapture/DebuggerCapture";
    private const string StepIntoCommandName = "Debug.StepInto";
    private const string StepOverCommandName = "Debug.StepOver";

    private readonly DTE2 dte;
    private readonly JoinableTaskFactory joinableTaskFactory;
    private readonly ICaptureFilePathProvider captureFilePathProvider;
    private readonly IScreenshotCaptureService screenshotCaptureService;
    private readonly IDebuggerVariableExportService variableExportService;
    private readonly IFeatureFlagService featureFlagService;
    private readonly IOutputWindowLogger logger;
    private DebuggerEvents? debuggerEvents;
    private CommandEvents? stepIntoCommandEvents;
    private CommandEvents? stepOverCommandEvents;
    private SnapshotTrigger? lastStepTrigger;
    private int captureInProgress;
    private bool disposed;

    public DebuggerBreakpointCaptureListener(
        DTE2 dte,
        JoinableTaskFactory joinableTaskFactory,
        ICaptureFilePathProvider captureFilePathProvider,
        IScreenshotCaptureService screenshotCaptureService,
        IDebuggerVariableExportService variableExportService,
        IFeatureFlagService featureFlagService,
        IOutputWindowLogger logger)
    {
        this.dte = dte ?? throw new ArgumentNullException(nameof(dte));
        this.joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        this.captureFilePathProvider = captureFilePathProvider ?? throw new ArgumentNullException(nameof(captureFilePathProvider));
        this.screenshotCaptureService = screenshotCaptureService ?? throw new ArgumentNullException(nameof(screenshotCaptureService));
        this.variableExportService = variableExportService ?? throw new ArgumentNullException(nameof(variableExportService));
        this.featureFlagService = featureFlagService ?? throw new ArgumentNullException(nameof(featureFlagService));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        this.debuggerEvents = this.dte.Events.DebuggerEvents;
        this.debuggerEvents.OnEnterBreakMode += this.OnEnterBreakMode;

        this.SubscribeToStepCommand(StepIntoCommandName, SnapshotTrigger.StepIn, ref this.stepIntoCommandEvents);
        this.SubscribeToStepCommand(StepOverCommandName, SnapshotTrigger.StepOver, ref this.stepOverCommandEvents);
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

        this.QueueCapture(this.GetVisualStudioWindowHandle(), trigger);
    }

    private void QueueCapture(IntPtr windowHandle, SnapshotTrigger trigger)
    {
        if (Interlocked.CompareExchange(ref this.captureInProgress, 1, 0) != 0)
        {
            this.joinableTaskFactory.RunAsync(() => this.LogCaptureSkippedAsync(trigger)).FileAndForget(TelemetryEventName);
            return;
        }

        this.joinableTaskFactory.RunAsync(() => this.CaptureWhenDebuggerUiIsReadyAsync(windowHandle, trigger)).FileAndForget(TelemetryEventName);
    }

    private async Task CaptureWhenDebuggerUiIsReadyAsync(IntPtr windowHandle, SnapshotTrigger trigger)
    {
        try
        {
            await this.WaitForDebuggerUiRenderAsync().ConfigureAwait(true);
            var fileSet = this.captureFilePathProvider.CreateFileSet(trigger);
            var screenshotTask = this.screenshotCaptureService.CaptureAsync(windowHandle, fileSet);
            var variablesTask = this.variableExportService.ExportAsync(fileSet);

            await Task.WhenAll(screenshotTask, variablesTask).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await this.LogSafelyAsync(exception).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref this.captureInProgress, 0);
        }
    }

    private async Task LogCaptureSkippedAsync(SnapshotTrigger trigger)
    {
        try
        {
            await this.logger.LogAsync("Skipped " + trigger + " capture because another capture is still running.").ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task WaitForDebuggerUiRenderAsync()
    {
        await this.joinableTaskFactory.SwitchToMainThreadAsync();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private IntPtr GetVisualStudioWindowHandle()
    {
        var windowHandle = this.dte.MainWindow.HWnd;
        return windowHandle != IntPtr.Zero ? windowHandle : NativeMethods.GetForegroundWindow();
    }

    private bool TryGetCaptureTrigger(dbgEventReason reason, out SnapshotTrigger trigger)
    {
        switch (reason)
        {
            case dbgEventReason.dbgEventReasonBreakpoint when this.featureFlagService.IsEnabled(CaptureFeature.Breakpoint):
                trigger = SnapshotTrigger.Breakpoint;
                return true;
            case dbgEventReason.dbgEventReasonStep when this.featureFlagService.IsEnabled(CaptureFeature.Step):
                return this.TryTakeLastStepTrigger(out trigger);
            case dbgEventReason.dbgEventReasonExceptionThrown when this.featureFlagService.IsEnabled(CaptureFeature.Exception):
            case dbgEventReason.dbgEventReasonExceptionNotHandled when this.featureFlagService.IsEnabled(CaptureFeature.Exception):
                trigger = SnapshotTrigger.Exception;
                return true;
            default:
                trigger = default;
                return false;
        }
    }

    private bool TryTakeLastStepTrigger(out SnapshotTrigger trigger)
    {
        if (!this.lastStepTrigger.HasValue)
        {
            trigger = default;
            return false;
        }

        trigger = this.lastStepTrigger.Value;
        this.lastStepTrigger = null;
        return true;
    }

    private void SubscribeToStepCommand(string commandName, SnapshotTrigger trigger, ref CommandEvents? commandEvents)
    {
        var command = this.dte.Commands.Item(commandName);
        commandEvents = this.dte.Events.CommandEvents[command.Guid, command.ID];

        if (trigger == SnapshotTrigger.StepIn)
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
        this.lastStepTrigger = SnapshotTrigger.StepIn;
    }

    private void OnStepOverBeforeExecute(string guid, int id, object customIn, object customOut, ref bool cancelDefault)
    {
        this.lastStepTrigger = SnapshotTrigger.StepOver;
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
