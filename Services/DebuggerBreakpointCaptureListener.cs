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
    private const string TelemetryEventName = "DebugCapture/BreakpointCapture";

    private readonly DTE2 dte;
    private readonly JoinableTaskFactory joinableTaskFactory;
    private readonly IScreenshotCaptureService screenshotCaptureService;
    private readonly IOutputWindowLogger logger;
    private DebuggerEvents? debuggerEvents;
    private bool disposed;

    public DebuggerBreakpointCaptureListener(
        DTE2 dte,
        JoinableTaskFactory joinableTaskFactory,
        IScreenshotCaptureService screenshotCaptureService,
        IOutputWindowLogger logger)
    {
        this.dte = dte ?? throw new ArgumentNullException(nameof(dte));
        this.joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        this.screenshotCaptureService = screenshotCaptureService ?? throw new ArgumentNullException(nameof(screenshotCaptureService));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        this.debuggerEvents = this.dte.Events.DebuggerEvents;
        this.debuggerEvents.OnEnterBreakMode += this.OnEnterBreakMode;
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

        this.disposed = true;
    }

    private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
    {
        if (reason != dbgEventReason.dbgEventReasonBreakpoint)
        {
            return;
        }

        this.joinableTaskFactory.RunAsync(this.CaptureVisualStudioWindowAsync).FileAndForget(TelemetryEventName);
    }

    private async Task CaptureVisualStudioWindowAsync()
    {
        try
        {
            var windowHandle = await this.GetVisualStudioWindowHandleAsync().ConfigureAwait(true);
            await this.screenshotCaptureService.CaptureAsync(windowHandle).ConfigureAwait(false);
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
