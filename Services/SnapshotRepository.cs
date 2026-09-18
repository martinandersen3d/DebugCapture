using DebugCapture.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DebugCapture.Services;

internal sealed class SnapshotRepository
{
    private const int FileBufferSize = 81920;

    public string SnapshotDirectory => GetSnapshotDirectory();

    public async Task<IReadOnlyList<SnapshotListItem>> LoadSnapshotsAsync(CancellationToken cancellationToken)
    {
        var directory = this.SnapshotDirectory;
        if (!Directory.Exists(directory))
        {
            return Array.Empty<SnapshotListItem>();
        }

        return await Task.Run(() => LoadSnapshots(directory, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<SnapshotListItem> LoadSnapshots(string directory, CancellationToken cancellationToken)
    {
        var items = new List<SnapshotListItem>();
        foreach (var filePath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = TryLoadSnapshot(filePath);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items
            .OrderBy(item => item.Snapshot.Timestamp)
            .ThenBy(item => item.SnapshotFilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SnapshotListItem? TryLoadSnapshot(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileBufferSize, useAsync: false);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var snapshot = JsonConvert.DeserializeObject<Snapshot>(json);
            return snapshot is null ? null : new SnapshotListItem(filePath, snapshot);
        }
        catch
        {
            return null;
        }
    }

    private static string GetSnapshotDirectory()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        }

        return Path.Combine(pictures, "VSScreenshots");
    }
}
