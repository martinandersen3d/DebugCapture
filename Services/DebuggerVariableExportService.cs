using EnvDTE;
using EnvDTE80;
using DebugCapture.Models;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DebugCapture.Services;

internal sealed class DebuggerVariableExportService : IDebuggerVariableExportService
{
    private const int FileBufferSize = 81920;
    private const int MemberDepthLimit = 3;
    private const int MemberCountLimit = 100;

    private readonly DTE2 dte;
    private readonly JoinableTaskFactory joinableTaskFactory;
    private readonly IOutputWindowLogger logger;
    private readonly ICaptureNotificationService notificationService;
    private static readonly JsonSerializerSettings JsonSerializerSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new StringEnumConverter() },
    };

    public DebuggerVariableExportService(DTE2 dte, JoinableTaskFactory joinableTaskFactory, IOutputWindowLogger logger, ICaptureNotificationService notificationService)
    {
        this.dte = dte ?? throw new ArgumentNullException(nameof(dte));
        this.joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
    }

    public async Task ExportAsync(CaptureFileSet fileSet)
    {
        if (fileSet is null)
        {
            throw new ArgumentNullException(nameof(fileSet));
        }

        try
        {
            var snapshot = await this.BuildSnapshotAsync(fileSet).ConfigureAwait(true);
            var content = JsonConvert.SerializeObject(snapshot, JsonSerializerSettings);
            await Task.Run(() => WriteFileAsync(fileSet.VariablesFilePath, content)).ConfigureAwait(false);
            this.notificationService.NotifyCaptureCompleted(fileSet.VariablesFilePath);
        }
        catch (Exception exception)
        {
            await this.LogSafelyAsync(exception).ConfigureAwait(false);
        }
    }

    private async Task<Snapshot> BuildSnapshotAsync(CaptureFileSet fileSet)
    {
        await this.joinableTaskFactory.SwitchToMainThreadAsync();

        var snapshot = new Snapshot
        {
            Trigger = fileSet.Trigger,
            Timestamp = fileSet.Timestamp,
            ImageFilePath = fileSet.ImageFilePath,
            SnapshotFilePath = fileSet.VariablesFilePath,
            FileName = GetActiveDocumentFileName(this.dte),
            Folder = GetActiveDocumentRelativeFolder(this.dte),
            LineNumber = GetActiveDocumentLine(this.dte),
            LineText = GetActiveDocumentLineText(this.dte),
            Info = BuildSnapshotInfo(this.dte),
        };

        var stackFrame = this.dte.Debugger?.CurrentStackFrame;
        if (stackFrame is null)
        {
            return snapshot;
        }

        if (fileSet.Trigger == SnapshotTrigger.Exception)
        {
            snapshot.Exception = BuildException(this.dte.Debugger);
        }

        snapshot.Locals = BuildExpressionList(() => stackFrame.Locals);
        snapshot.Autos = BuildExpressionList(() => stackFrame.Arguments);
        snapshot.CallStack = BuildCallStack(this.dte.Debugger?.CurrentThread?.StackFrames, stackFrame, snapshot);

        return snapshot;
    }

    private static SnapshotInfo BuildSnapshotInfo(DTE2 dte)
    {
        return new SnapshotInfo
        {
            ProjectName = GetSafeValue(() => dte.ActiveDocument?.ProjectItem?.ContainingProject?.Name),
            SolutionName = GetSafeValue(() => Path.GetFileNameWithoutExtension(dte.Solution?.FullName)),
            ProcessName = GetSafeValue(() => dte.Debugger?.CurrentProcess?.Name),
            ThreadName = GetSafeValue(() => dte.Debugger?.CurrentThread?.Name),
        };
    }

    private static SnapshotException BuildException(Debugger debugger)
    {
        return BuildException(debugger, "$exception", 0);
    }

    private static SnapshotException BuildException(Debugger debugger, string expressionText, int depth)
    {
        var snapshotException = new SnapshotException();

        try
        {
            var expression = debugger.GetExpression(expressionText, UseAutoExpandRules: true, Timeout: 1000);
            if (expression is null || !expression.IsValidValue)
            {
                snapshotException.Message = "No current exception expression is available.";
                return snapshotException;
            }

            snapshotException.TypeName = GetDebuggerExpressionValue(debugger, expressionText + ".GetType().FullName");
            if (string.IsNullOrWhiteSpace(snapshotException.TypeName))
            {
                snapshotException.TypeName = GetSafeValue(() => expression.Type);
            }

            snapshotException.Message = GetDebuggerExpressionValue(debugger, expressionText + ".Message");
            if (string.IsNullOrWhiteSpace(snapshotException.Message))
            {
                snapshotException.Message = GetSafeValue(() => expression.Value);
            }

            snapshotException.Source = GetDebuggerExpressionValue(debugger, expressionText + ".Source");
            snapshotException.TargetSite = GetDebuggerExpressionValue(debugger, expressionText + ".TargetSite");
            snapshotException.HResult = GetDebuggerExpressionValue(debugger, expressionText + ".HResult");
            snapshotException.StackTrace = GetDebuggerExpressionValue(debugger, expressionText + ".StackTrace");

            var counter = new SnapshotMemberCounter();
            snapshotException.Members = BuildExpressionMembers(expression, 0, counter);
            snapshotException.MembersTruncated = counter.Truncated;

            if (depth < MemberDepthLimit)
            {
                var innerException = TryGetExpression(debugger, expressionText + ".InnerException");
                if (innerException is not null && innerException.IsValidValue && !string.Equals(GetSafeValue(() => innerException.Value), "null", StringComparison.OrdinalIgnoreCase))
                {
                    snapshotException.InnerException = BuildException(debugger, expressionText + ".InnerException", depth + 1);
                }
            }
        }
        catch (Exception exception)
        {
            snapshotException.Message = "Unable to read exception details: " + exception.Message;
        }

        return snapshotException;
    }

    private static Expression? TryGetExpression(Debugger debugger, string expressionText)
    {
        try
        {
            return debugger.GetExpression(expressionText, UseAutoExpandRules: true, Timeout: 1000);
        }
        catch
        {
            return null;
        }
    }

    private static string GetDebuggerExpressionValue(Debugger debugger, string expressionText)
    {
        var expression = TryGetExpression(debugger, expressionText);
        if (expression is null || !expression.IsValidValue)
        {
            return string.Empty;
        }

        return GetSafeValue(() => expression.Value);
    }

    private static List<SnapshotCallStackFrame> BuildCallStack(StackFrames? stackFrames, StackFrame currentStackFrame, Snapshot snapshot)
    {
        var frames = new List<SnapshotCallStackFrame>();
        if (stackFrames is null || stackFrames.Count == 0)
        {
            return frames;
        }

        var currentFunctionName = GetSafeValue(() => currentStackFrame.FunctionName);
        var index = 0;
        foreach (StackFrame frame in stackFrames)
        {
            var isCurrentFrame = index == 0 || string.Equals(GetSafeValue(() => frame.FunctionName), currentFunctionName, StringComparison.Ordinal);
            frames.Add(new SnapshotCallStackFrame
            {
                FunctionName = GetSafeValue(() => frame.FunctionName),
                Module = GetSafeValue(() => frame.Module),
                File = isCurrentFrame ? CombinePath(snapshot.Folder, snapshot.FileName) : string.Empty,
                Line = isCurrentFrame && snapshot.LineNumber > 0 ? snapshot.LineNumber : null,
                IsCurrentFrame = isCurrentFrame,
            });

            index++;
        }

        return frames;
    }

    private static List<SnapshotProperty> BuildExpressionList(Func<Expressions> expressionsFactory)
    {
        var properties = new List<SnapshotProperty>();

        try
        {
            var expressions = expressionsFactory();
            if (expressions is null || expressions.Count == 0)
            {
                return properties;
            }

            var counter = new SnapshotMemberCounter();
            foreach (Expression expression in expressions)
            {
                properties.Add(BuildSnapshotProperty(expression, 0, counter));
            }
        }
        catch (Exception exception)
        {
            properties.Add(new SnapshotProperty
            {
                Name = "Section",
                Value = "<error: Unable to read section: " + exception.Message + ">",
            });
        }

        return properties;
    }

    private static List<SnapshotProperty> BuildExpressionMembers(Expression expression, int depth, SnapshotMemberCounter counter)
    {
        var children = new List<SnapshotProperty>();

        try
        {
            var dataMembers = expression.DataMembers;
            if (dataMembers is null || dataMembers.Count == 0)
            {
                return children;
            }

            foreach (Expression member in dataMembers)
            {
                if (counter.Count >= MemberCountLimit)
                {
                    counter.Truncated = true;
                    return children;
                }

                children.Add(BuildSnapshotProperty(member, depth + 1, counter));
            }
        }
        catch (Exception exception)
        {
            children.Add(new SnapshotProperty
            {
                Name = "Members",
                Value = "<error: Unable to read members: " + exception.Message + ">",
            });
        }

        return children;
    }

    private static SnapshotProperty BuildSnapshotProperty(Expression expression, int depth, SnapshotMemberCounter counter)
    {
        counter.Count++;
        var property = new SnapshotProperty
        {
            Name = GetSafeValue(() => expression.Name),
            Value = GetSafeValue(() => expression.Value),
            Type = GetSafeValue(() => expression.Type),
        };

        if (depth >= MemberDepthLimit)
        {
            counter.Truncated = true;
        }
        else
        {
            var children = BuildExpressionMembers(expression, depth, counter);
            if (children.Count > 0)
            {
                property.Children = children;
            }
        }

        return property;
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

    private static string GetActiveDocumentRelativeFolder(DTE2 dte)
    {
        try
        {
            var fullName = dte.ActiveDocument?.FullName;
            return string.IsNullOrWhiteSpace(fullName) ? string.Empty : Path.GetDirectoryName(fullName) ?? string.Empty;
        }
        catch (Exception exception)
        {
            return "<error: " + exception.Message + ">";
        }
    }

    private static string GetActiveDocumentFileName(DTE2 dte)
    {
        try
        {
            var fullName = dte.ActiveDocument?.FullName;
            return string.IsNullOrWhiteSpace(fullName) ? string.Empty : Path.GetFileName(fullName);
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

    private static string GetActiveDocumentLineText(DTE2 dte)
    {
        try
        {
            if (dte.ActiveDocument?.Object("TextDocument") is not TextDocument textDocument ||
                dte.ActiveDocument.Selection is not TextSelection selection)
            {
                return string.Empty;
            }

            var line = selection.ActivePoint.Line;
            var endLine = textDocument.EndPoint.Line;
            var editPoint = textDocument.StartPoint.CreateEditPoint();
            return line < endLine
                ? editPoint.GetLines(line, line + 1).TrimEnd('\r', '\n')
                : editPoint.GetLines(line, line).TrimEnd('\r', '\n');
        }
        catch (Exception exception)
        {
            return "<error: " + exception.Message + ">";
        }
    }

    private static string CombinePath(string folder, string fileName)
    {
        return string.IsNullOrEmpty(folder) ? fileName : Path.Combine(folder, fileName);
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

    private sealed class SnapshotMemberCounter
    {
        public int Count { get; set; }

        public bool Truncated { get; set; }
    }
}
