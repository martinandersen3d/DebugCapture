using DebugCapture.Commands;
using DebugCapture.Models;
using DebugCapture.Services;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DebugCapture.ToolWindows;

internal sealed class SnapshotToolWindowViewModel : ObservableObject, IDisposable
{
    private readonly SnapshotRepository repository;
    private CancellationTokenSource? refreshCancellationTokenSource;

    public SnapshotToolWindowViewModel()
        : this(new SnapshotRepository())
    {
    }

    public SnapshotToolWindowViewModel(SnapshotRepository repository)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.RefreshCommand = new AsyncRelayCommand(this.RefreshAsync);
        this.FirstCommand = new RelayCommand(this.SelectFirst, () => this.State.Snapshots.Count > 0);
        this.PreviousCommand = new RelayCommand(this.SelectPrevious, () => this.State.SelectedIndex > 0);
        this.NextCommand = new RelayCommand(this.SelectNext, () => this.State.SelectedIndex >= 0 && this.State.SelectedIndex < this.State.Snapshots.Count - 1);
        this.LastCommand = new RelayCommand(this.SelectLast, () => this.State.Snapshots.Count > 0);
        this.OpenSourceCommand = new AsyncRelayCommand(this.OpenSelectedSourceAsync, () => this.State.SelectedSnapshot is not null);
        this.OpenInExplorerCommand = new RelayCommand(this.OpenSelectedInExplorer, () => this.State.SelectedSnapshot is not null);
    }

    public UiState State { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand FirstCommand { get; }

    public ICommand PreviousCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand LastCommand { get; }

    public ICommand OpenSourceCommand { get; }

    public ICommand OpenInExplorerCommand { get; }

    public int SnapshotCount => this.State.Snapshots.Count;

    public int MaxSnapshotIndex => Math.Max(0, this.State.Snapshots.Count - 1);

    public Snapshot? SelectedSnapshot => this.State.SelectedSnapshot?.Snapshot;

    public IEnumerable<SnapshotProperty>? Locals => this.SelectedSnapshot?.Locals;

    public IEnumerable<SnapshotProperty>? Autos => this.SelectedSnapshot?.Autos;

    public IEnumerable<SnapshotProperty>? Watch1 => this.SelectedSnapshot?.Watch1;

    public IEnumerable<SnapshotProperty>? Watch2 => this.SelectedSnapshot?.Watch2;

    public IEnumerable<SnapshotProperty>? Watch3 => this.SelectedSnapshot?.Watch3;

    public IEnumerable<SnapshotProperty>? Watch4 => this.SelectedSnapshot?.Watch4;

    public IEnumerable<SnapshotProperty>? ExceptionMembers => this.SelectedSnapshot?.Exception?.Members;

    public IEnumerable<SnapshotCallStackFrame>? CallStack => this.SelectedSnapshot?.CallStack;

    public async Task InitializeAsync()
    {
        await this.RefreshAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        this.refreshCancellationTokenSource?.Cancel();
        this.refreshCancellationTokenSource?.Dispose();
        this.refreshCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = this.refreshCancellationTokenSource.Token;

        this.State.IsLoading = true;
        this.State.StatusMessage = "Loading snapshots...";
        this.RaiseCommandStates();

        try
        {
            var snapshots = await this.repository.LoadSnapshotsAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            this.State.Snapshots.Clear();
            foreach (var snapshot in snapshots)
            {
                this.State.Snapshots.Add(snapshot);
            }

            this.State.SelectedIndex = this.State.Snapshots.Count > 0 ? this.State.Snapshots.Count - 1 : -1;
            this.SyncSelectedSnapshotFromIndex();
            this.State.StatusMessage = this.State.Snapshots.Count == 0
                ? "No snapshots found in " + this.repository.SnapshotDirectory
                : this.State.Snapshots.Count + " snapshots loaded";
        }
        catch (OperationCanceledException)
        {
            this.State.StatusMessage = "Refresh canceled";
        }
        catch (Exception exception)
        {
            this.State.StatusMessage = "Failed to load snapshots: " + exception.Message;
        }
        finally
        {
            this.State.IsLoading = false;
            this.OnStateChanged();
            this.RaiseCommandStates();
        }
    }

    public void SelectSnapshot(SnapshotListItem? snapshot)
    {
        this.State.SelectedSnapshot = snapshot;
        this.State.SelectedIndex = snapshot is null ? -1 : this.State.Snapshots.IndexOf(snapshot);
        this.OnStateChanged();
        this.RaiseCommandStates();
    }

    public void SelectSnapshotByIndex(int index)
    {
        if (this.State.Snapshots.Count == 0)
        {
            this.State.SelectedIndex = -1;
            this.State.SelectedSnapshot = null;
        }
        else
        {
            var boundedIndex = Math.Max(0, Math.Min(index, this.State.Snapshots.Count - 1));
            this.State.SelectedIndex = boundedIndex;
            this.State.SelectedSnapshot = this.State.Snapshots[boundedIndex];
        }

        this.OnStateChanged();
        this.RaiseCommandStates();
    }

    public void Dispose()
    {
        this.refreshCancellationTokenSource?.Cancel();
        this.refreshCancellationTokenSource?.Dispose();
    }

    private void SelectFirst()
    {
        this.SelectSnapshotByIndex(0);
    }

    private void SelectPrevious()
    {
        this.SelectSnapshotByIndex(this.State.SelectedIndex - 1);
    }

    private void SelectNext()
    {
        this.SelectSnapshotByIndex(this.State.SelectedIndex + 1);
    }

    private void SelectLast()
    {
        this.SelectSnapshotByIndex(this.State.Snapshots.Count - 1);
    }

    private void SyncSelectedSnapshotFromIndex()
    {
        this.State.SelectedSnapshot = this.State.SelectedIndex >= 0 && this.State.SelectedIndex < this.State.Snapshots.Count
            ? this.State.Snapshots[this.State.SelectedIndex]
            : null;
    }

    private async Task OpenSelectedSourceAsync()
    {
        var snapshot = this.SelectedSnapshot;
        if (snapshot is null)
        {
            return;
        }

        var filePath = Path.Combine(snapshot.Folder ?? string.Empty, snapshot.FileName ?? string.Empty);
        if (!File.Exists(filePath))
        {
            this.State.StatusMessage = "Source file not found: " + filePath;
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) is not DTE2 dte)
        {
            this.State.StatusMessage = "DTE service is unavailable.";
            return;
        }

        dte.ItemOperations.OpenFile(filePath);
        if (dte.ActiveDocument?.Selection is TextSelection selection && snapshot.LineNumber > 0)
        {
            selection.GotoLine(snapshot.LineNumber, Select: true);
        }
    }

    private void OpenSelectedInExplorer()
    {
        var filePath = this.State.SelectedSnapshot?.SnapshotFilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            this.State.StatusMessage = "Snapshot file not found.";
            return;
        }

        System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + filePath + "\"");
    }

    private void RaiseCommandStates()
    {
        RaiseCanExecuteChanged(this.FirstCommand);
        RaiseCanExecuteChanged(this.PreviousCommand);
        RaiseCanExecuteChanged(this.NextCommand);
        RaiseCanExecuteChanged(this.LastCommand);
        RaiseCanExecuteChanged(this.OpenSourceCommand);
        RaiseCanExecuteChanged(this.OpenInExplorerCommand);
    }

    private void OnStateChanged()
    {
        this.OnPropertyChanged(nameof(this.SnapshotCount));
        this.OnPropertyChanged(nameof(this.MaxSnapshotIndex));
        this.OnPropertyChanged(nameof(this.SelectedSnapshot));
        this.OnPropertyChanged(nameof(this.Locals));
        this.OnPropertyChanged(nameof(this.Autos));
        this.OnPropertyChanged(nameof(this.Watch1));
        this.OnPropertyChanged(nameof(this.Watch2));
        this.OnPropertyChanged(nameof(this.Watch3));
        this.OnPropertyChanged(nameof(this.Watch4));
        this.OnPropertyChanged(nameof(this.ExceptionMembers));
        this.OnPropertyChanged(nameof(this.CallStack));
    }

    private static void RaiseCanExecuteChanged(ICommand command)
    {
        switch (command)
        {
            case RelayCommand relayCommand:
                relayCommand.RaiseCanExecuteChanged();
                break;
            case AsyncRelayCommand asyncRelayCommand:
                asyncRelayCommand.RaiseCanExecuteChanged();
                break;
        }
    }
}
