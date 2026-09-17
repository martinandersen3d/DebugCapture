using System;
using System.Globalization;
using System.IO;
using DebugCapture.Models;

namespace DebugCapture.Services;

internal sealed class CaptureFilePathProvider : ICaptureFilePathProvider
{
    public CaptureFileSet CreateFileSet(SnapshotTrigger trigger)
    {
        var directory = GetScreenshotDirectory();
        Directory.CreateDirectory(directory);

        var timestamp = DateTimeOffset.Now;
        var basePath = GetUniqueBasePath(directory, trigger, timestamp);

        return new CaptureFileSet(
            trigger,
            timestamp,
            basePath + ".png",
            basePath + ".json");
    }

    private static string GetUniqueBasePath(string directory, SnapshotTrigger trigger, DateTimeOffset timestamp)
    {
        var basePath = Path.Combine(directory, GetBaseFileName(trigger, timestamp));

        for (var counter = 1; File.Exists(basePath + ".png") || File.Exists(basePath + ".json"); counter++)
        {
            basePath = Path.Combine(directory, GetBaseFileName(trigger, timestamp, counter));
        }

        return basePath;
    }

    private static string GetScreenshotDirectory()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        }

        return Path.Combine(pictures, "VSScreenshots");
    }

    private static string GetBaseFileName(SnapshotTrigger trigger, DateTimeOffset timestamp, int? counter = null)
    {
        var counterSuffix = counter.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "_{0:000}", counter.Value)
            : string.Empty;

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd__HH-mm-ss-fff}_{1}{2}",
            timestamp,
            trigger.GetFileSuffix(),
            counterSuffix);
    }
}
