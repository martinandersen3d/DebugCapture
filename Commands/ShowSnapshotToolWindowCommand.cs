using DebugCapture.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;

namespace DebugCapture.Commands;

internal sealed class ShowSnapshotToolWindowCommand
{
    private readonly AsyncPackage package;

    private ShowSnapshotToolWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        this.package = package ?? throw new ArgumentNullException(nameof(package));

        var commandId = new CommandID(CommandIds.CommandSet, CommandIds.ShowSnapshotToolWindow);
        var command = new MenuCommand(this.Execute, commandId);
        commandService.AddCommand(command);
    }

    public static async Task InitializeAsync(AsyncPackage package, CancellationToken cancellationToken)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await package.GetServiceAsync(typeof(IMenuCommandService)).ConfigureAwait(true) is OleMenuCommandService commandService)
        {
            _ = new ShowSnapshotToolWindowCommand(package, commandService);
        }
    }

    private void Execute(object sender, EventArgs e)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await this.package.ShowToolWindowAsync(typeof(SnapshotToolWindow), 0, create: true, this.package.DisposalToken).ConfigureAwait(true);
        });
    }
}
