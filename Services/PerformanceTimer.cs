using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

namespace DebugCapture.Services;

internal sealed class PerformanceTimer : IDisposable
{
    private readonly IOutputWindowLogger logger;
    private readonly string operationName;
    private readonly string? detail;
    private readonly Stopwatch stopwatch;
    private bool disposed;

    public PerformanceTimer(IOutputWindowLogger logger, string operationName, string? detail = null)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.operationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
        this.detail = detail;
        this.stopwatch = Stopwatch.StartNew();
    }

    public static PerformanceTimer Start(IOutputWindowLogger logger, string operationName, string? detail = null)
    {
        return new PerformanceTimer(logger, operationName, detail);
    }

    public static void LogMetric(IOutputWindowLogger logger, string operationName, string detail)
    {
        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        if (string.IsNullOrWhiteSpace(operationName))
        {
            return;
        }

        LogSafely(logger, FormatMetricMessage(operationName, detail));
    }

    public long ElapsedMilliseconds => this.stopwatch.ElapsedMilliseconds;

    public void LogCheckpoint(string checkpointName)
    {
        if (string.IsNullOrWhiteSpace(checkpointName))
        {
            return;
        }

        LogSafely(this.logger, FormatMessage(this.operationName + ":" + checkpointName, this.stopwatch.ElapsedMilliseconds, this.detail));
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.stopwatch.Stop();
        LogSafely(this.logger, FormatMessage(this.operationName, this.stopwatch.ElapsedMilliseconds, this.detail));
    }

    private static string FormatMessage(string operationName, long elapsedMilliseconds, string? detail)
    {
        var message = string.Format(
            CultureInfo.InvariantCulture,
            "[Perf] {0} completed in {1} ms",
            operationName,
            elapsedMilliseconds);

        return string.IsNullOrWhiteSpace(detail) ? message : message + " | " + detail;
    }

    private static string FormatMetricMessage(string operationName, string detail)
    {
        return string.IsNullOrWhiteSpace(detail)
            ? "[Perf] " + operationName
            : "[Perf] " + operationName + " | " + detail;
    }

    private static void LogSafely(IOutputWindowLogger logger, string message)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await logger.LogAsync(message).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }
        catch
        {
        }
    }
}
