using DebugCapture.Models;
using System.Collections.ObjectModel;

namespace DebugCapture.ToolWindows;

internal sealed class UiState : ObservableObject
{
    private SnapshotListItem? selectedSnapshot;
    private int selectedIndex = -1;
    private bool isLoading;
    private bool isSideBySideLayout = true;
    private string statusMessage = string.Empty;

    public ObservableCollection<SnapshotListItem> Snapshots { get; } = new();

    public SnapshotListItem? SelectedSnapshot
    {
        get => this.selectedSnapshot;
        set => this.SetProperty(ref this.selectedSnapshot, value);
    }

    public int SelectedIndex
    {
        get => this.selectedIndex;
        set => this.SetProperty(ref this.selectedIndex, value);
    }

    public bool IsLoading
    {
        get => this.isLoading;
        set => this.SetProperty(ref this.isLoading, value);
    }

    public bool IsSideBySideLayout
    {
        get => this.isSideBySideLayout;
        set => this.SetProperty(ref this.isSideBySideLayout, value);
    }

    public string StatusMessage
    {
        get => this.statusMessage;
        set => this.SetProperty(ref this.statusMessage, value);
    }
}
