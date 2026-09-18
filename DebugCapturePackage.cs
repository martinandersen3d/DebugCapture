using DebugCapture.Commands;
using DebugCapture.Services;
using DebugCapture.ToolWindows;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DebugCapture;
/// <summary>
/// This is the class that implements the package exposed by this assembly.
/// </summary>
/// <remarks>
/// <para>
/// The minimum requirement for a class to be considered a valid package for Visual Studio
/// is to implement the IVsPackage interface and register itself with the shell.
/// This package uses the helper classes defined inside the Managed Package Framework (MPF)
/// to do it: it derives from the Package class that provides the implementation of the
/// IVsPackage interface and uses the registration attributes defined in the framework to
/// register itself and its components with the shell. These attributes tell the pkgdef creation
/// utility what data to put into .pkgdef file.
/// </para>
/// <para>
/// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
/// </para>
/// </remarks>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(UIContextGuids80.Debugging, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(SnapshotToolWindow), Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindOutput)]
[Guid(DebugCapturePackage.PackageGuidString)]
public sealed class DebugCapturePackage : AsyncPackage
{
    /// <summary>
    /// DebugCapturePackage GUID string.
    /// </summary>
    public const string PackageGuidString = "522c1f58-2cff-43c4-8f68-96a201f8b4ce";

    private OutputWindowLogger? logger;
    private DebuggerBreakpointCaptureListener? breakpointCaptureListener;

    #region Package Members

    /// <summary>
    /// Initialization of the package; this method is called right after the package is sited, so this is the place
    /// where you can put all the initialization code that rely on services provided by VisualStudio.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
    /// <param name="progress">A provider for progress updates.</param>
    /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await ShowSnapshotToolWindowCommand.InitializeAsync(this, cancellationToken);

        this.logger = new OutputWindowLogger(this);
        await this.logger.InitializeAsync(cancellationToken);

        var dte = await this.GetDteAsync(cancellationToken);
        if (dte is null)
        {
            await this.logger.LogAsync("DTE service is unavailable; breakpoint capture listener was not started.", cancellationToken);
            return;
        }

        var notificationService = new CaptureNotificationService();
        var featureFlagService = new FeatureFlagService();
        var captureFilePathProvider = new CaptureFilePathProvider();
        var boundsProvider = new WindowBoundsProvider();
        var screenshotCaptureService = new ScreenshotCaptureService(boundsProvider, this.logger, notificationService);
        var variableExportService = new DebuggerVariableExportService(dte, this.JoinableTaskFactory, this.logger);

        this.breakpointCaptureListener = new DebuggerBreakpointCaptureListener(
            dte,
            this.JoinableTaskFactory,
            captureFilePathProvider,
            screenshotCaptureService,
            variableExportService,
            featureFlagService,
            this.logger);

        await this.breakpointCaptureListener.StartAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && this.breakpointCaptureListener is not null)
        {
            this.JoinableTaskFactory.Run(async () =>
            {
                await this.JoinableTaskFactory.SwitchToMainThreadAsync();
                this.breakpointCaptureListener.Dispose();
                this.breakpointCaptureListener = null;
            });
        }

        base.Dispose(disposing);
    }

    private async Task<DTE2?> GetDteAsync(CancellationToken cancellationToken)
    {
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        return await this.GetServiceAsync(typeof(SDTE)).ConfigureAwait(true) as DTE2;
    }

    #endregion
}
