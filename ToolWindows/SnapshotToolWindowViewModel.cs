using DebugCapture.Commands;
using DebugCapture.Models;
using DebugCapture.Services;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;

namespace DebugCapture.ToolWindows;

internal sealed class SnapshotToolWindowViewModel : ObservableObject, IDisposable
{
    private readonly SnapshotRepository repository;
    private readonly ICaptureNotificationService notificationService;
    private readonly HashSet<string> expandedSnapshotDetailKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource? refreshCancellationTokenSource;
    private bool disposed;
    private bool isWindowVisible;

    public SnapshotToolWindowViewModel()
        : this(new SnapshotRepository(), CaptureNotificationService.Shared)
    {
    }

    public SnapshotToolWindowViewModel(SnapshotRepository repository, ICaptureNotificationService notificationService)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        this.RefreshCommand = new AsyncRelayCommand(this.RefreshAsync);
        this.FirstCommand = new RelayCommand(this.SelectFirst, () => this.State.Snapshots.Count > 0);
        this.PreviousCommand = new RelayCommand(this.SelectPrevious, () => this.State.SelectedIndex > 0);
        this.NextCommand = new RelayCommand(this.SelectNext, () => this.State.SelectedIndex >= 0 && this.State.SelectedIndex < this.State.Snapshots.Count - 1);
        this.LastCommand = new RelayCommand(this.SelectLast, () => this.State.Snapshots.Count > 0);
        this.OpenSourceCommand = new AsyncRelayCommand(this.OpenSelectedSourceAsync, () => this.State.SelectedSnapshot is not null);
        this.OpenInExplorerCommand = new RelayCommand(this.OpenSelectedInExplorer, () => this.State.SelectedSnapshot is not null);

        this.notificationService.CaptureCompleted += this.OnCaptureCompleted;
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

    public bool IsWindowVisible
    {
        get => this.isWindowVisible;
        set => this.SetProperty(ref this.isWindowVisible, value);
    }

    public IEnumerable<SnapshotProperty>? Locals => this.SelectedSnapshot?.Locals;

    public IEnumerable<SnapshotProperty>? Autos => this.SelectedSnapshot?.Autos;

    public IEnumerable<SnapshotProperty>? Watch1 => this.SelectedSnapshot?.Watch1;

    public IEnumerable<SnapshotProperty>? Watch2 => this.SelectedSnapshot?.Watch2;

    public IEnumerable<SnapshotProperty>? Watch3 => this.SelectedSnapshot?.Watch3;

    public IEnumerable<SnapshotProperty>? Watch4 => this.SelectedSnapshot?.Watch4;

    public IEnumerable<SnapshotProperty>? ExceptionMembers => this.SelectedSnapshot?.Exception?.Members;

    public IEnumerable<SnapshotCallStackFrame>? CallStack => this.SelectedSnapshot?.CallStack;

    public ObservableCollection<SnapshotDetailTreeNode> SnapshotDetails { get; } = new();

    public async Task InitializeAsync()
    {
        await this.RefreshAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.CancelRefresh();
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
        this.RebuildSnapshotDetails();
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

        this.RebuildSnapshotDetails();
        this.OnStateChanged();
        this.RaiseCommandStates();
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.notificationService.CaptureCompleted -= this.OnCaptureCompleted;
        this.CancelRefresh();
    }

    private void OnCaptureCompleted(object? sender, CaptureCompletedEventArgs e)
    {
        if (this.disposed || !this.IsWindowVisible || !IsJsonSnapshotFile(e.FilePath))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            if (!this.disposed && this.IsWindowVisible)
            {
                this.RefreshCommand.Execute(null);
            }
        }), DispatcherPriority.Background);
    }

    private static bool IsJsonSnapshotFile(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase);
    }

    private void CancelRefresh()
    {
        var cancellationTokenSource = this.refreshCancellationTokenSource;
        this.refreshCancellationTokenSource = null;

        if (cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cancellationTokenSource.Dispose();
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
        this.RebuildSnapshotDetails();
    }

    private void RebuildSnapshotDetails()
    {
        this.SnapshotDetails.Clear();

        var snapshot = this.SelectedSnapshot;
        if (snapshot is null)
        {
            return;
        }

        this.SnapshotDetails.Add(this.InitializeNodeExpansion(CreatePropertyGroup("Locals", "Locals", snapshot.Locals)));
        this.SnapshotDetails.Add(this.InitializeNodeExpansion(CreatePropertyGroup("Autos", "Autos", snapshot.Autos)));
        this.SnapshotDetails.Add(this.InitializeNodeExpansion(CreatePropertyGroup("Watch 1", "Watch 1", snapshot.Watch1)));
        this.SnapshotDetails.Add(this.InitializeNodeExpansion(CreatePropertyGroup("Watch 2", "Watch 2", snapshot.Watch2)));
        this.SnapshotDetails.Add(this.InitializeNodeExpansion(CreatePropertyGroup("Watch 3", "Watch 3", snapshot.Watch3)));
        this.SnapshotDetails.Add(this.InitializeNodeExpansion(CreatePropertyGroup("Watch 4", "Watch 4", snapshot.Watch4)));
        this.SnapshotDetails.Add(this.InitializeNodeExpansion(CreateExceptionGroup(snapshot.Exception)));
        this.SnapshotDetails.Add(this.InitializeNodeExpansion(CreateCallStackGroup(snapshot.CallStack)));
    }

    private SnapshotDetailTreeNode InitializeNodeExpansion(SnapshotDetailTreeNode node)
    {
        node.IsExpanded = this.expandedSnapshotDetailKeys.Contains(node.ExpansionKey);
        node.ExpansionChanged = this.OnSnapshotDetailNodeExpansionChanged;

        foreach (var child in node.Children)
        {
            this.InitializeNodeExpansion(child);
        }

        return node;
    }

    private void OnSnapshotDetailNodeExpansionChanged(SnapshotDetailTreeNode node)
    {
        if (string.IsNullOrEmpty(node.ExpansionKey))
        {
            return;
        }

        if (node.IsExpanded)
        {
            this.expandedSnapshotDetailKeys.Add(node.ExpansionKey);
        }
        else
        {
            this.expandedSnapshotDetailKeys.Remove(node.ExpansionKey);
        }
    }

    private static SnapshotDetailTreeNode CreatePropertyGroup(string name, string expansionKey, IEnumerable<SnapshotProperty>? properties)
    {
        var group = new SnapshotDetailTreeNode { Name = name, ExpansionKey = expansionKey };
        if (properties is null)
        {
            return group;
        }

        foreach (var property in properties)
        {
            group.Children.Add(CreatePropertyNode(property, expansionKey));
        }

        return group;
    }

    private static SnapshotDetailTreeNode CreatePropertyNode(SnapshotProperty property, string parentKey)
    {
        var expansionKey = CombineExpansionKey(parentKey, property.Name);
        var node = new SnapshotDetailTreeNode
        {
            Name = property.Name,
            Value = property.Value,
            Type = property.Type,
            ExpansionKey = expansionKey,
            SnapshotProperty = property,
        };

        if (property.Children is not null)
        {
            foreach (var child in property.Children)
            {
                node.Children.Add(CreatePropertyNode(child, expansionKey));
            }
        }

        return node;
    }

    private static SnapshotDetailTreeNode CreateExceptionGroup(SnapshotException? exception)
    {
        var group = new SnapshotDetailTreeNode { Name = "Exception", ExpansionKey = "Exception" };
        if (exception is null)
        {
            return group;
        }

        group.Children.Add(new SnapshotDetailTreeNode { Name = "Message", Value = exception.Message, Type = "string", ExpansionKey = "Exception/Message" });
        group.Children.Add(new SnapshotDetailTreeNode { Name = "StackTrace", Value = exception.StackTrace, Type = "string", ExpansionKey = "Exception/StackTrace" });
        return group;
    }

    private static SnapshotDetailTreeNode CreateCallStackGroup(IEnumerable<SnapshotCallStackFrame>? frames)
    {
        var group = new SnapshotDetailTreeNode { Name = "CallStack", ExpansionKey = "CallStack" };
        if (frames is null)
        {
            return group;
        }

        foreach (var frame in frames)
        {
            var frameName = frame.FunctionName ?? string.Empty;
            group.Children.Add(new SnapshotDetailTreeNode
            {
                Name = frameName,
                Value = FormatCallStackLocation(frame),
                Type = frame.Module,
                ExpansionKey = CombineExpansionKey("CallStack", frameName),
            });
        }

        return group;
    }

    private static string FormatCallStackLocation(SnapshotCallStackFrame frame)
    {
        if (string.IsNullOrEmpty(frame.File))
        {
            return string.Empty;
        }

        return frame.Line.HasValue
            ? frame.File + ":" + frame.Line.Value
            : frame.File;
    }

    private static string CombineExpansionKey(string parentKey, string? name)
    {
        return parentKey + "/" + (name ?? string.Empty);
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
