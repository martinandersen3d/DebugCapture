using System;

namespace DebugCapture.Models;

internal sealed class SnapshotListItem
{
    public SnapshotListItem(string snapshotFilePath, Snapshot snapshot)
    {
        this.SnapshotFilePath = snapshotFilePath ?? throw new ArgumentNullException(nameof(snapshotFilePath));
        this.Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public string SnapshotFilePath { get; }

    public Snapshot Snapshot { get; }

    public bool IsSelectedForAction { get; set; }

    public string SourceLocation => string.IsNullOrWhiteSpace(this.Snapshot.FileName)
        ? this.SnapshotFilePath
        : this.Snapshot.FileName + ":L" + this.Snapshot.LineNumber;

    public string Trigger => this.Snapshot.Trigger.ToString().ToUpperInvariant();

    public string Time => this.Snapshot.Timestamp.ToString("HH:mm:ss");
}
