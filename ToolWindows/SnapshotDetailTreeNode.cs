using DebugCapture.Models;
using System;
using System.Collections.ObjectModel;

namespace DebugCapture.ToolWindows;

internal sealed class SnapshotDetailTreeNode
{
    private bool isExpanded;

    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string ExpansionKey { get; set; } = string.Empty;

    public bool IsExpanded
    {
        get => this.isExpanded;
        set
        {
            if (this.isExpanded == value)
            {
                return;
            }

            this.isExpanded = value;
            this.ExpansionChanged?.Invoke(this);
        }
    }

    public SnapshotProperty? SnapshotProperty { get; set; }

    public Action<SnapshotDetailTreeNode>? ExpansionChanged { get; set; }

    public ObservableCollection<SnapshotDetailTreeNode> Children { get; } = new();
}
