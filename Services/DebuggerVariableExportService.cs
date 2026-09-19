using EnvDTE;
using EnvDTE80;
using DebugCapture.Models;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DebugCapture.Services;

internal sealed class DebuggerVariableExportService : IDebuggerVariableExportService
{
    private const int FileBufferSize = 81920;
    private static readonly SnapshotExtractionOptions ExtractionOptions = new();

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

        var extractionClock = new SnapshotExtractionClock(ExtractionOptions);

        if (fileSet.Trigger == SnapshotTrigger.Exception)
        {
            snapshot.Exception = BuildException(this.dte.Debugger, ExtractionOptions);
        }

        var isExceptionCapture = fileSet.Trigger == SnapshotTrigger.Exception;
        snapshot.Locals = BuildExpressionList(() => stackFrame.Locals, extractionClock, isExceptionCapture);
        snapshot.Autos = BuildExpressionList(() => stackFrame.Arguments, extractionClock, isExceptionCapture);
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

    private static SnapshotException BuildException(Debugger debugger, SnapshotExtractionOptions options)
    {
        return BuildException(debugger, "$exception", options);
    }

    private static SnapshotException BuildException(Debugger debugger, string expressionText, SnapshotExtractionOptions options)
    {
        var snapshotException = new SnapshotException();

        try
        {
            snapshotException.Message = TruncateValue(GetDebuggerExpressionValue(debugger, expressionText + ".Message"), options.MaxValueLength);
            snapshotException.StackTrace = TruncateValue(GetDebuggerExpressionValue(debugger, expressionText + ".StackTrace"), options.MaxValueLength);
            if (options.MaxExceptionMembers <= 0)
            {
                snapshotException.MembersTruncated = true;
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
            return debugger.GetExpression(expressionText, UseAutoExpandRules: false, Timeout: 200);
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

    private static List<SnapshotProperty> BuildExpressionList(Func<Expressions> expressionsFactory, SnapshotExtractionClock extractionClock, bool isExceptionCapture)
    {
        var properties = new List<SnapshotProperty>();
        var rootEntries = new List<SnapshotRootEntry>();

        try
        {
            var expressions = expressionsFactory();
            if (expressions is null || expressions.Count == 0)
            {
                return properties;
            }

            foreach (Expression expression in expressions)
            {
                if (extractionClock.IsTimeBudgetExhausted)
                {
                    return properties;
                }

                var budget = new SnapshotExtractionBudget(ExtractionOptions, extractionClock);
                var property = BuildSnapshotProperty(expression, 0, budget, isExceptionCapture, expandChildren: false);
                if (property is not null)
                {
                    properties.Add(property);
                    rootEntries.Add(new SnapshotRootEntry(expression, property, budget));
                }
            }

            foreach (var rootEntry in rootEntries)
            {
                if (extractionClock.IsTimeBudgetExhausted)
                {
                    break;
                }

                ExpandSnapshotProperty(rootEntry.Expression, rootEntry.Property, 0, rootEntry.Budget, isExceptionCapture);
            }
        }
        catch (Exception exception)
        {
            properties.Add(new SnapshotProperty
            {
                Name = "Section",
                Value = TruncateValue("<error: Unable to read section: " + exception.Message + ">", extractionClock.Options.MaxValueLength),
            });
        }

        return properties;
    }

    private static List<SnapshotProperty> BuildExpressionMembers(Expression expression, int depth, SnapshotExtractionBudget budget, bool isExceptionCapture, out int? childrenTotalCount)
    {
        var children = new List<SnapshotProperty>();
        childrenTotalCount = null;

        try
        {
            if (budget.IsExhausted || depth >= budget.Options.MaxDepth)
            {
                return children;
            }

            var dataMembers = expression.DataMembers;
            if (dataMembers is null || dataMembers.Count == 0)
            {
                return children;
            }

            childrenTotalCount = dataMembers.Count;
            var capturedChildren = 0;
            foreach (Expression member in dataMembers)
            {
                if (budget.IsExhausted || capturedChildren >= budget.Options.MaxChildrenPerNode)
                {
                    return children;
                }

                var child = BuildSnapshotProperty(member, depth + 1, budget, isExceptionCapture);
                if (child is null)
                {
                    return children;
                }

                children.Add(child);
                capturedChildren++;
            }
        }
        catch (Exception exception)
        {
            children.Add(new SnapshotProperty
            {
                Name = "Members",
                Value = TruncateValue("<error: Unable to read members: " + exception.Message + ">", budget.Options.MaxValueLength),
            });
        }

        return children;
    }

    private static SnapshotProperty? BuildSnapshotProperty(Expression expression, int depth, SnapshotExtractionBudget budget, bool isExceptionCapture)
    {
        return BuildSnapshotProperty(expression, depth, budget, isExceptionCapture, expandChildren: true);
    }

    private static SnapshotProperty? BuildSnapshotProperty(Expression expression, int depth, SnapshotExtractionBudget budget, bool isExceptionCapture, bool expandChildren)
    {
        if (!budget.TryConsumeNode())
        {
            return null;
        }

        var property = new SnapshotProperty
        {
            Name = GetSafeValue(() => expression.Name, budget.Options.MaxValueLength),
            Value = GetSafeValue(() => expression.Value, budget.Options.MaxValueLength),
            Type = GetSafeValue(() => expression.Type, budget.Options.MaxValueLength),
        };

        if (!expandChildren)
        {
            return property;
        }

        ExpandSnapshotProperty(expression, property, depth, budget, isExceptionCapture);
        return property;
    }

    private static void ExpandSnapshotProperty(Expression expression, SnapshotProperty property, int depth, SnapshotExtractionBudget budget, bool isExceptionCapture)
    {
        if (depth >= budget.Options.MaxDepth || budget.IsExhausted || IsExceptionObject(property, isExceptionCapture))
        {
            return;
        }

        var children = BuildExpressionMembers(expression, depth, budget, isExceptionCapture, out var childrenTotalCount);
        if (childrenTotalCount.HasValue)
        {
            property.ChildrenTotalCount = childrenTotalCount;
            property.ChildrenSnapshotCount = children.Count;
        }

        if (children.Count > 0)
        {
            property.Children = children;
        }
    }

    private static bool IsExceptionObject(SnapshotProperty property, bool isExceptionCapture)
    {
        if (!isExceptionCapture)
        {
            return false;
        }

        return string.Equals(property.Name, "$exception", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(property.Type) && property.Type.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0);
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

    private static string GetSafeValue(Func<string> valueFactory, int maxLength)
    {
        return TruncateValue(GetSafeValue(valueFactory), maxLength);
    }

    private static string TruncateValue(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0 || value.Length <= maxLength)
        {
            return value;
        }

        return value.Substring(0, maxLength) + "... <SnapshotCutoff:10.000Chars>";
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

    private sealed class SnapshotExtractionOptions
    {
        public int MaxDepth { get; set; } = 2;

        public int MaxChildrenPerNode { get; set; } = 100;

        public int MaxNodesPerRoot { get; set; } = 1000;

        public int MaxValueLength { get; set; } = 10000;

        public int MaxExtractionMilliseconds { get; set; } = 1500;

        public int MaxExceptionMembers { get; set; }
    }

    private sealed class SnapshotExtractionClock
    {
        private readonly System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        public SnapshotExtractionClock(SnapshotExtractionOptions options)
        {
            this.Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public SnapshotExtractionOptions Options { get; }

        public bool IsTimeBudgetExhausted => this.stopwatch.ElapsedMilliseconds >= this.Options.MaxExtractionMilliseconds;
    }

    private sealed class SnapshotExtractionBudget
    {
        private readonly SnapshotExtractionClock extractionClock;

        public SnapshotExtractionBudget(SnapshotExtractionOptions options, SnapshotExtractionClock extractionClock)
        {
            this.Options = options ?? throw new ArgumentNullException(nameof(options));
            this.extractionClock = extractionClock ?? throw new ArgumentNullException(nameof(extractionClock));
        }

        public SnapshotExtractionOptions Options { get; }

        public int CapturedNodes { get; private set; }

        public bool IsNodeBudgetExhausted => this.CapturedNodes >= this.Options.MaxNodesPerRoot;

        public bool IsTimeBudgetExhausted => this.extractionClock.IsTimeBudgetExhausted;

        public bool IsExhausted => this.IsNodeBudgetExhausted || this.IsTimeBudgetExhausted;

        public bool TryConsumeNode()
        {
            if (this.IsExhausted)
            {
                return false;
            }

            this.CapturedNodes++;
            return true;
        }
    }

    private sealed class SnapshotRootEntry
    {
        public SnapshotRootEntry(Expression expression, SnapshotProperty property, SnapshotExtractionBudget budget)
        {
            this.Expression = expression ?? throw new ArgumentNullException(nameof(expression));
            this.Property = property ?? throw new ArgumentNullException(nameof(property));
            this.Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        }

        public Expression Expression { get; }

        public SnapshotProperty Property { get; }

        public SnapshotExtractionBudget Budget { get; }
    }
}
