using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace DebugCapture.Services;

internal sealed class OutputWindowLogger : IOutputWindowLogger
{
    private static readonly Guid PaneGuid = new("72f6bb9f-5a14-4072-a32a-20d91945a229");
    private const string PaneTitle = "DebugCapture";

    private readonly AsyncPackage package;
    private IVsOutputWindowPane? pane;

    public OutputWindowLogger(AsyncPackage package)
    {
        this.package = package ?? throw new ArgumentNullException(nameof(package));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await this.package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var outputWindow = await this.package.GetServiceAsync(typeof(SVsOutputWindow)).ConfigureAwait(true) as IVsOutputWindow;
        if (outputWindow is null)
        {
            return;
        }

        var paneGuid = PaneGuid;
        outputWindow.CreatePane(ref paneGuid, PaneTitle, fInitVisible: 0, fClearWithSolution: 0);
        outputWindow.GetPane(ref paneGuid, out this.pane);
    }

    public Task LogAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        return this.LogAsync(exception.ToString(), cancellationToken);
    }

    public async Task LogAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await this.package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        this.pane?.OutputStringThreadSafe(FormatMessage(message));
    }

    private static string FormatMessage(string message)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}{2}",
            DateTimeOffset.Now,
            message,
            Environment.NewLine);
    }
}
