using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Threading;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DebugCapture.Services;

internal sealed class DebuggerVariableExportService : IDebuggerVariableExportService
{
    private const int FileBufferSize = 81920;
    private const int ExceptionMemberDepthLimit = 3;
    private const int ExceptionMemberCountLimit = 100;

    private readonly DTE2 dte;
    private readonly JoinableTaskFactory joinableTaskFactory;
    private readonly IOutputWindowLogger logger;

    public DebuggerVariableExportService(DTE2 dte, JoinableTaskFactory joinableTaskFactory, IOutputWindowLogger logger)
    {
        this.dte = dte ?? throw new ArgumentNullException(nameof(dte));
        this.joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExportAsync(CaptureFileSet fileSet)
    {
        if (fileSet is null)
        {
            throw new ArgumentNullException(nameof(fileSet));
        }

        try
        {
            var content = await this.BuildSnapshotTextAsync(fileSet).ConfigureAwait(true);
            await Task.Run(() => WriteFileAsync(fileSet.VariablesFilePath, content)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await this.LogSafelyAsync(exception).ConfigureAwait(false);
        }
    }

    private async Task<string> BuildSnapshotTextAsync(CaptureFileSet fileSet)
    {
        await this.joinableTaskFactory.SwitchToMainThreadAsync();

        var builder = new StringBuilder();
        builder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "=== DEBUGGER SNAPSHOT: {0:yyyy-MM-dd HH:mm:ss.fff} ===",
            fileSet.Timestamp));
        builder.AppendLine();

        var stackFrame = this.dte.Debugger?.CurrentStackFrame;
        if (stackFrame is null)
        {
            builder.AppendLine("No current debugger stack frame is available.");
            return builder.ToString();
        }

        AppendSourceLocation(builder, this.dte);
        builder.AppendLine();

        if (fileSet.Trigger == ScreenshotCaptureTrigger.Exception)
        {
            AppendExceptionDetails(builder, this.dte.Debugger);
            builder.AppendLine();
        }

        AppendExpressions(builder, "LOCALS", () => stackFrame.Locals);
        builder.AppendLine();
        AppendExpressions(builder, "AUTOS", () => stackFrame.Arguments);
        builder.AppendLine();
        AppendCallStack(builder, this.dte.Debugger?.CurrentThread?.StackFrames);

        return builder.ToString();
    }

    private static void AppendSourceLocation(StringBuilder builder, DTE2 dte)
    {
        builder.AppendLine("File: " + GetActiveDocumentRelativePath(dte));
        builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "Line: {0}", GetActiveDocumentLine(dte)));
    }

    private static void AppendExceptionDetails(StringBuilder builder, Debugger debugger)
    {
        builder.AppendLine("--- EXCEPTION ---");

        try
        {
            var exceptionExpression = debugger.GetExpression("$exception", UseAutoExpandRules: true, Timeout: 1000);
            if (exceptionExpression is null || !exceptionExpression.IsValidValue)
            {
                builder.AppendLine("No current exception expression is available.");
                return;
            }

            AppendDebuggerExpression(builder, exceptionExpression, 0, new ExceptionMemberCounter());
            AppendExceptionExpressionValue(builder, debugger, "$exception.GetType().FullName");
            AppendExceptionExpressionValue(builder, debugger, "$exception.Message");
            AppendExceptionExpressionValue(builder, debugger, "$exception.Source");
            AppendExceptionExpressionValue(builder, debugger, "$exception.TargetSite");
            AppendExceptionExpressionValue(builder, debugger, "$exception.HResult");
            AppendExceptionExpressionValue(builder, debugger, "$exception.StackTrace");
            AppendExceptionExpressionValue(builder, debugger, "$exception.InnerException");
            AppendExceptionExpressionValue(builder, debugger, "$exception.Data");
            AppendExceptionExpressionValue(builder, debugger, "$exception.ToString()");
        }
        catch (Exception exception)
        {
            builder.AppendLine("Unable to read exception details: " + exception.Message);
        }
    }

    private static void AppendExceptionExpressionValue(StringBuilder builder, Debugger debugger, string expressionText)
    {
        try
        {
            var expression = debugger.GetExpression(expressionText, UseAutoExpandRules: true, Timeout: 1000);
            if (expression is null || !expression.IsValidValue)
            {
                return;
            }

            builder.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} = {1}",
                expressionText,
                GetSafeValue(() => expression.Value)));
        }
        catch (Exception exception)
        {
            builder.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} = <error: {1}>",
                expressionText,
                exception.Message));
        }
    }

    private static void AppendDebuggerExpression(StringBuilder builder, Expression expression, int depth, ExceptionMemberCounter counter)
    {
        if (depth > ExceptionMemberDepthLimit || counter.Count >= ExceptionMemberCountLimit)
        {
            return;
        }

        counter.Count++;
        var indent = new string(' ', depth * 2);
        builder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1} {2} = {3}",
            indent,
            GetSafeValue(() => expression.Type),
            GetSafeValue(() => expression.Name),
            GetSafeValue(() => expression.Value)));

        try
        {
            var dataMembers = expression.DataMembers;
            if (dataMembers is null || dataMembers.Count == 0)
            {
                return;
            }

            foreach (Expression member in dataMembers)
            {
                if (counter.Count >= ExceptionMemberCountLimit)
                {
                    builder.AppendLine(indent + "  ...");
                    return;
                }

                AppendDebuggerExpression(builder, member, depth + 1, counter);
            }
        }
        catch (Exception exception)
        {
            builder.AppendLine(indent + "Unable to read exception members: " + exception.Message);
        }
    }

    private static void AppendCallStack(StringBuilder builder, StackFrames? stackFrames)
    {
        builder.AppendLine("--- CALL STACK ---");

        if (stackFrames is null || stackFrames.Count == 0)
        {
            builder.AppendLine("(none)");
            return;
        }

        foreach (StackFrame frame in stackFrames)
        {
            AppendStackFrame(builder, frame);
        }
    }

    private static void AppendStackFrame(StringBuilder builder, StackFrame frame)
    {
        try
        {
            builder.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} [{1}]",
                GetSafeValue(() => frame.FunctionName),
                GetSafeValue(() => frame.Module)));
        }
        catch (Exception exception)
        {
            builder.AppendLine("Unable to read stack frame: " + exception.Message);
        }
    }

    private static void AppendExpressions(StringBuilder builder, string title, Func<Expressions> expressionsFactory)
    {
        builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "--- {0} ---", title));

        try
        {
            var expressions = expressionsFactory();
            if (expressions is null || expressions.Count == 0)
            {
                builder.AppendLine("(none)");
                return;
            }

            foreach (Expression expression in expressions)
            {
                AppendExpression(builder, expression);
            }
        }
        catch (Exception exception)
        {
            builder.AppendLine("Unable to read section: " + exception.Message);
        }
    }

    private static void AppendExpression(StringBuilder builder, Expression expression)
    {
        try
        {
            builder.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} = {2}",
                GetSafeValue(() => expression.Type),
                GetSafeValue(() => expression.Name),
                GetSafeValue(() => expression.Value)));
        }
        catch (Exception exception)
        {
            builder.AppendLine("Unable to read variable: " + exception.Message);
        }
    }

    private static string GetSafeValue(Func<string> valueFactory)
    {
        try
        {
            return valueFactory() ?? string.Empty;
        }
        catch (Exception exception)
        {
            return "<error: " + exception.Message + ">";
        }
    }

    private static string GetActiveDocumentRelativePath(DTE2 dte)
    {
        try
        {
            return GetRelativeFilePath(dte.ActiveDocument?.FullName, dte.Solution?.Projects);
        }
        catch (Exception exception)
        {
            return "<error: " + exception.Message + ">";
        }
    }

    private static int GetActiveDocumentLine(DTE2 dte)
    {
        try
        {
            return dte.ActiveDocument?.Selection is TextSelection selection
                ? selection.ActivePoint.Line
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetRelativeFilePath(string? filePath, Projects? projects)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        var projectDirectory = GetContainingProjectDirectory(filePath, projects);
        return string.IsNullOrWhiteSpace(projectDirectory)
            ? filePath
            : MakeRelativePath(projectDirectory, filePath);
    }

    private static string? GetContainingProjectDirectory(string filePath, Projects? projects)
    {
        if (projects is null)
        {
            return null;
        }

        string? bestMatch = null;
        foreach (Project project in projects)
        {
            var projectDirectory = GetProjectDirectory(project);
            if (string.IsNullOrWhiteSpace(projectDirectory) || !IsPathInsideDirectory(filePath, projectDirectory))
            {
                continue;
            }

            if (bestMatch is null || projectDirectory.Length > bestMatch.Length)
            {
                bestMatch = projectDirectory;
            }
        }

        return bestMatch;
    }

    private static string? GetProjectDirectory(Project project)
    {
        try
        {
            return string.IsNullOrWhiteSpace(project.FullName)
                ? null
                : Path.GetDirectoryName(project.FullName);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPathInsideDirectory(string filePath, string directory)
    {
        var normalizedFilePath = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedFilePath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeRelativePath(string directory, string filePath)
    {
        var directoryUri = new Uri(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var fileUri = new Uri(Path.GetFullPath(filePath));
        return Uri.UnescapeDataString(directoryUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
    }

    private static async Task WriteFileAsync(string filePath, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileBufferSize, useAsync: true);
        await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
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

    private sealed class ExceptionMemberCounter
    {
        public int Count { get; set; }
    }
}
