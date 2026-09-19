using DebugCapture.Models;
using System.Collections.ObjectModel;

namespace DebugCapture.ToolWindows;

internal sealed class SnapshotDetailTreeNode
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public SnapshotProperty? SnapshotProperty { get; set; }

    public ObservableCollection<SnapshotDetailTreeNode> Children { get; } = new();
}
