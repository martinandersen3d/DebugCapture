using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace DebugCapture.Services;

internal sealed class ScreenshotCaptureService : IScreenshotCaptureService
{
    private const int FileBufferSize = 81920;

    private readonly IWindowBoundsProvider boundsProvider;
    private readonly IOutputWindowLogger logger;
    private readonly ICaptureNotificationService notificationService;

    public ScreenshotCaptureService(
        IWindowBoundsProvider boundsProvider,
        IOutputWindowLogger logger,
        ICaptureNotificationService notificationService)
    {
        this.boundsProvider = boundsProvider ?? throw new ArgumentNullException(nameof(boundsProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
    }

    public Task CaptureAsync(IntPtr windowHandle, ScreenshotCaptureTrigger trigger)
    {
        return Task.Run(() => this.CaptureCoreAsync(windowHandle, trigger));
    }

    private async Task CaptureCoreAsync(IntPtr windowHandle, ScreenshotCaptureTrigger trigger)
    {
        try
        {
            if (!this.boundsProvider.TryGetWindowBounds(windowHandle, out var bounds))
            {
                await this.LogSafelyAsync("Unable to determine Visual Studio window bounds.").ConfigureAwait(false);
                return;
            }

            var directory = GetScreenshotDirectory();
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var filePath = GetNextFilePath(directory, trigger);

            var pngBytes = CapturePngBytes(bounds);
            await WriteFileAsync(filePath, pngBytes).ConfigureAwait(false);

            this.notificationService.NotifyCaptureCompleted(filePath);
        }
        catch (Exception exception)
        {
            await this.LogSafelyAsync(exception).ConfigureAwait(false);
        }
    }

    private static byte[] CapturePngBytes(Rectangle bounds)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static async Task WriteFileAsync(string filePath, byte[] pngBytes)
    {
        using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileBufferSize, useAsync: true);
        await stream.WriteAsync(pngBytes, 0, pngBytes.Length).ConfigureAwait(false);
    }

    private static string GetNextFilePath(string directory, ScreenshotCaptureTrigger trigger)
    {
        var timestamp = DateTime.Now;
        var filePath = Path.Combine(directory, GetFileName(timestamp, trigger));

        for (var counter = 1; File.Exists(filePath); counter++)
        {
            filePath = Path.Combine(directory, GetFileName(timestamp, trigger, counter));
        }

        return filePath;
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

    private static string GetFileName(DateTime timestamp, ScreenshotCaptureTrigger trigger, int? counter = null)
    {
        var counterSuffix = counter.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "_{0:000}", counter.Value)
            : string.Empty;

        return string.Format(
            CultureInfo.InvariantCulture,
            "VS_Capture_{0:yyyyMMdd_HHmmss_fff}-{1}{2}.png",
            timestamp,
            trigger.GetFileSuffix(),
            counterSuffix);
    }

    private async Task LogSafelyAsync(Exception exception)
    {
        try
        {
            await this.logger.LogAsync(exception).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task LogSafelyAsync(string message)
    {
        try
        {
            await this.logger.LogAsync(message).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
