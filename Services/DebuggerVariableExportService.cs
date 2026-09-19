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

        using var timer = PerformanceTimer.Start(this.logger, "Snapshot export " + fileSet.Trigger);

        try
        {
            var snapshot = await this.BuildSnapshotAsync(fileSet).ConfigureAwait(true);
            timer.LogCheckpoint("DTE snapshot built");

            var content = JsonConvert.SerializeObject(snapshot, JsonSerializerSettings);
            timer.LogCheckpoint("JSON serialized");
            PerformanceTimer.LogMetric(this.logger, "Snapshot JSON size " + fileSet.Trigger, "bytes=" + Encoding.UTF8.GetByteCount(content));

            await Task.Run(() => WriteFileAsync(fileSet.VariablesFilePath, content)).ConfigureAwait(false);
            timer.LogCheckpoint("JSON file written");

            this.notificationService.NotifyCaptureCompleted(fileSet.VariablesFilePath);
            timer.LogCheckpoint("Notification sent");
        }
        catch (Exception exception)
        {
            await this.LogSafelyAsync(exception).ConfigureAwait(false);
        }
    }

    private async Task<Snapshot> BuildSnapshotAsync(CaptureFileSet fileSet)
    {
        using var timer = PerformanceTimer.Start(this.logger, "Build snapshot " + fileSet.Trigger);

        await this.joinableTaskFactory.SwitchToMainThreadAsync();
        timer.LogCheckpoint("Main thread");

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
        timer.LogCheckpoint("Header");

        var stackFrame = this.dte.Debugger?.CurrentStackFrame;
        if (stackFrame is null)
        {
            return snapshot;
        }

        var extractionClock = new SnapshotExtractionClock(ExtractionOptions);
        var metrics = new SnapshotExtractionMetrics();

        if (fileSet.Trigger == SnapshotTrigger.Exception)
        {
            snapshot.Exception = BuildException(this.dte.Debugger, ExtractionOptions);
            timer.LogCheckpoint("Exception summary");
        }

        var extractionStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var isExceptionCapture = fileSet.Trigger == SnapshotTrigger.Exception;
        snapshot.Locals = BuildExpressionList("Locals", () => stackFrame.Locals, extractionClock, isExceptionCapture, metrics);
        timer.LogCheckpoint("Locals");

        snapshot.Autos = BuildExpressionList("Autos", () => stackFrame.Arguments, extractionClock, isExceptionCapture, metrics);
        timer.LogCheckpoint("Autos");
        extractionStopwatch.Stop();

        snapshot.CallStack = BuildCallStack(this.dte.Debugger?.CurrentThread?.StackFrames, stackFrame, snapshot);
        timer.LogCheckpoint("CallStack");
        PerformanceTimer.LogMetric(this.logger, "Snapshot extraction metrics " + fileSet.Trigger, metrics.ToLogDetail());
        PerformanceTimer.LogMetric(this.logger, "Snapshot slow roots " + fileSet.Trigger, metrics.ToRootTimingLogDetail());
        PerformanceTimer.LogMetric(this.logger, "Snapshot extraction accounted time " + fileSet.Trigger, metrics.ToAccountedTimeLogDetail(extractionStopwatch.ElapsedTicks));

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

    private static SnapshotException BuildException(EnvDTE.Debugger debugger, SnapshotExtractionOptions options)
    {
        return BuildException(debugger, "$exception", options);
    }

    private static SnapshotException BuildException(EnvDTE.Debugger debugger, string expressionText, SnapshotExtractionOptions options)
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

    private static Expression? TryGetExpression(EnvDTE.Debugger debugger, string expressionText)
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

    private static string GetDebuggerExpressionValue(EnvDTE.Debugger debugger, string expressionText)
    {
        var expression = TryGetExpression(debugger, expressionText);
        if (expression is null || !expression.IsValidValue)
        {
            return string.Empty;
        }

        return GetSafeValue(() => expression.Value);
    }

    private static List<SnapshotCallStackFrame> BuildCallStack(StackFrames? stackFrames, EnvDTE.StackFrame currentStackFrame, Snapshot snapshot)
    {
        var frames = new List<SnapshotCallStackFrame>();
        if (stackFrames is null || stackFrames.Count == 0)
        {
            return frames;
        }

        var currentFunctionName = GetSafeValue(() => currentStackFrame.FunctionName);
        var index = 0;
        foreach (EnvDTE.StackFrame frame in stackFrames)
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

    private static List<SnapshotProperty> BuildExpressionList(string sectionName, Func<Expressions> expressionsFactory, SnapshotExtractionClock extractionClock, bool isExceptionCapture, SnapshotExtractionMetrics metrics)
    {
        var properties = new List<SnapshotProperty>();
        var rootEntries = new List<SnapshotRootEntry>();

        try
        {
            var expressionsAccessStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var expressions = expressionsFactory();
            expressionsAccessStopwatch.Stop();
            metrics.AddExpressionsAccess(expressionsAccessStopwatch.ElapsedTicks);

            var expressionsCountStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var expressionsCount = expressions?.Count ?? 0;
            expressionsCountStopwatch.Stop();
            metrics.AddExpressionsCount(expressionsCountStopwatch.ElapsedTicks);

            if (expressions is null || expressionsCount == 0)
            {
                return properties;
            }

            metrics.AddRootCount(sectionName, expressionsCount);

            var rootEnumerationStopwatch = System.Diagnostics.Stopwatch.StartNew();
            foreach (Expression expression in expressions)
            {
                rootEnumerationStopwatch.Stop();
                metrics.AddExpressionsEnumeration(rootEnumerationStopwatch.ElapsedTicks);

                if (extractionClock.IsTimeBudgetExhausted)
                {
                    metrics.AddTimeBudgetHit();
                    return properties;
                }

                var budget = new SnapshotExtractionBudget(ExtractionOptions, extractionClock);
                var scalarStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var property = BuildSnapshotProperty(expression, 0, budget, isExceptionCapture, metrics, expandChildren: false);
                scalarStopwatch.Stop();
                if (property is not null)
                {
                    properties.Add(property);
                    var rootTiming = metrics.AddRootTiming(sectionName, property.Name, scalarStopwatch.ElapsedTicks);
                    rootEntries.Add(new SnapshotRootEntry(expression, property, budget, rootTiming));
                }
                else
                {
                    metrics.AddSkippedNode();
                    metrics.AddBudgetHit(budget);
                }

                rootEnumerationStopwatch.Restart();
            }
            rootEnumerationStopwatch.Stop();

            foreach (var rootEntry in rootEntries)
            {
                if (extractionClock.IsTimeBudgetExhausted)
                {
                    metrics.AddTimeBudgetHit();
                    break;
                }

                var expansionStopwatch = System.Diagnostics.Stopwatch.StartNew();
                ExpandSnapshotProperty(rootEntry.Expression, rootEntry.Property, 0, rootEntry.Budget, isExceptionCapture, metrics);
                expansionStopwatch.Stop();
                rootEntry.RootTiming.AddExpansion(expansionStopwatch.ElapsedTicks);
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

    private static List<SnapshotProperty> BuildExpressionMembers(Expression expression, int depth, SnapshotExtractionBudget budget, bool isExceptionCapture, SnapshotExtractionMetrics metrics, out int? childrenTotalCount)
    {
        var children = new List<SnapshotProperty>();
        childrenTotalCount = null;

        try
        {
            if (budget.IsExhausted || depth >= budget.Options.MaxDepth)
            {
                if (depth >= budget.Options.MaxDepth)
                {
                    metrics.AddMaxDepthHit();
                }

                metrics.AddBudgetHit(budget);
                return children;
            }

            var dataMembersStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var dataMembers = expression.DataMembers;
            dataMembersStopwatch.Stop();
            metrics.AddDataMembers(dataMembersStopwatch.ElapsedTicks);

            var dataMembersCountStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var dataMembersCount = dataMembers?.Count ?? 0;
            dataMembersCountStopwatch.Stop();
            metrics.AddDataMembersCount(dataMembersCountStopwatch.ElapsedTicks);

            if (dataMembers is null || dataMembersCount == 0)
            {
                return children;
            }

            childrenTotalCount = dataMembersCount;
            var capturedChildren = 0;
            var dataMembersEnumerationStopwatch = System.Diagnostics.Stopwatch.StartNew();
            foreach (Expression member in dataMembers)
            {
                dataMembersEnumerationStopwatch.Stop();
                metrics.AddDataMembersEnumeration(dataMembersEnumerationStopwatch.ElapsedTicks);

                if (budget.IsExhausted || capturedChildren >= budget.Options.MaxChildrenPerNode)
                {
                    metrics.AddSkippedNode();
                    metrics.AddBudgetHit(budget);
                    return children;
                }

                var child = BuildSnapshotProperty(member, depth + 1, budget, isExceptionCapture, metrics);
                if (child is null)
                {
                    metrics.AddSkippedNode();
                    metrics.AddBudgetHit(budget);
                    return children;
                }

                children.Add(child);
                capturedChildren++;
                dataMembersEnumerationStopwatch.Restart();
            }
            dataMembersEnumerationStopwatch.Stop();
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

    private static SnapshotProperty? BuildSnapshotProperty(Expression expression, int depth, SnapshotExtractionBudget budget, bool isExceptionCapture, SnapshotExtractionMetrics metrics)
    {
        return BuildSnapshotProperty(expression, depth, budget, isExceptionCapture, metrics, expandChildren: true);
    }

    private static SnapshotProperty? BuildSnapshotProperty(Expression expression, int depth, SnapshotExtractionBudget budget, bool isExceptionCapture, SnapshotExtractionMetrics metrics, bool expandChildren)
    {
        if (!budget.TryConsumeNode())
        {
            return null;
        }

        metrics.AddCapturedNode();

        var property = new SnapshotProperty
        {
            Name = GetTimedSafeValue(() => expression.Name, budget.Options.MaxValueLength, metrics.AddNameRead),
            Value = GetTimedSafeValue(() => expression.Value, budget.Options.MaxValueLength, metrics.AddValueRead),
            Type = GetTimedSafeValue(() => expression.Type, budget.Options.MaxValueLength, metrics.AddTypeRead),
        };

        if (!expandChildren)
        {
            return property;
        }

        ExpandSnapshotProperty(expression, property, depth, budget, isExceptionCapture, metrics);
        return property;
    }

    private static void ExpandSnapshotProperty(Expression expression, SnapshotProperty property, int depth, SnapshotExtractionBudget budget, bool isExceptionCapture, SnapshotExtractionMetrics metrics)
    {
        if (depth >= budget.Options.MaxDepth || budget.IsExhausted || IsExceptionObject(property, isExceptionCapture))
        {
            if (depth >= budget.Options.MaxDepth)
            {
                metrics.AddMaxDepthHit();
            }

            metrics.AddBudgetHit(budget);
            return;
        }

        var children = BuildExpressionMembers(expression, depth, budget, isExceptionCapture, metrics, out var childrenTotalCount);
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

    private static string GetTimedSafeValue(Func<string> valueFactory, int maxLength, Action<long> elapsedTicksRecorder)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return GetSafeValue(valueFactory, maxLength);
        }
        finally
        {
            stopwatch.Stop();
            elapsedTicksRecorder(stopwatch.ElapsedTicks);
        }
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
        public int MaxDepth { get; set; } = 1;

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
        public SnapshotRootEntry(Expression expression, SnapshotProperty property, SnapshotExtractionBudget budget, SnapshotExtractionMetrics.SnapshotRootTiming rootTiming)
        {
            this.Expression = expression ?? throw new ArgumentNullException(nameof(expression));
            this.Property = property ?? throw new ArgumentNullException(nameof(property));
            this.Budget = budget ?? throw new ArgumentNullException(nameof(budget));
            this.RootTiming = rootTiming ?? throw new ArgumentNullException(nameof(rootTiming));
        }

        public Expression Expression { get; }

        public SnapshotProperty Property { get; }

        public SnapshotExtractionBudget Budget { get; }

        public SnapshotExtractionMetrics.SnapshotRootTiming RootTiming { get; }
    }

    private sealed class SnapshotExtractionMetrics
    {
        private long nameTicks;
        private long valueTicks;
        private long typeTicks;
        private long dataMembersTicks;
        private long dataMembersCountTicks;
        private long dataMembersEnumerationTicks;
        private long expressionsAccessTicks;
        private long expressionsCountTicks;
        private long expressionsEnumerationTicks;
        private readonly List<SnapshotRootTiming> rootTimings = new();

        public int LocalRootCount { get; private set; }

        public int AutoRootCount { get; private set; }

        public int CapturedNodes { get; private set; }

        public int SkippedNodes { get; private set; }

        public int MaxDepthHits { get; private set; }

        public int PerRootBudgetHits { get; private set; }

        public int TimeBudgetHits { get; private set; }

        public int DataMembersCalls { get; private set; }

        public int NameReads { get; private set; }

        public int ValueReads { get; private set; }

        public int TypeReads { get; private set; }

        public int ExpressionsAccessCalls { get; private set; }

        public int ExpressionsCountReads { get; private set; }

        public int ExpressionsEnumerationCalls { get; private set; }

        public int DataMembersCountReads { get; private set; }

        public int DataMembersEnumerationCalls { get; private set; }

        public bool HitExtractionCutoff => this.TimeBudgetHits > 0;

        public long AccountedTicks => this.nameTicks +
            this.valueTicks +
            this.typeTicks +
            this.dataMembersTicks +
            this.dataMembersCountTicks +
            this.dataMembersEnumerationTicks +
            this.expressionsAccessTicks +
            this.expressionsCountTicks +
            this.expressionsEnumerationTicks;

        public void AddRootCount(string sectionName, int count)
        {
            if (string.Equals(sectionName, "Locals", StringComparison.OrdinalIgnoreCase))
            {
                this.LocalRootCount += count;
            }
            else if (string.Equals(sectionName, "Autos", StringComparison.OrdinalIgnoreCase))
            {
                this.AutoRootCount += count;
            }
        }

        public void AddCapturedNode()
        {
            this.CapturedNodes++;
        }

        public void AddSkippedNode()
        {
            this.SkippedNodes++;
        }

        public void AddMaxDepthHit()
        {
            this.MaxDepthHits++;
        }

        public void AddBudgetHit(SnapshotExtractionBudget budget)
        {
            if (budget.IsTimeBudgetExhausted)
            {
                this.TimeBudgetHits++;
            }

            if (budget.IsNodeBudgetExhausted)
            {
                this.PerRootBudgetHits++;
            }
        }

        public void AddTimeBudgetHit()
        {
            this.TimeBudgetHits++;
        }

        public void AddDataMembers(long elapsedTicks)
        {
            this.DataMembersCalls++;
            this.dataMembersTicks += elapsedTicks;
        }

        public void AddDataMembersCount(long elapsedTicks)
        {
            this.DataMembersCountReads++;
            this.dataMembersCountTicks += elapsedTicks;
        }

        public void AddDataMembersEnumeration(long elapsedTicks)
        {
            this.DataMembersEnumerationCalls++;
            this.dataMembersEnumerationTicks += elapsedTicks;
        }

        public void AddExpressionsAccess(long elapsedTicks)
        {
            this.ExpressionsAccessCalls++;
            this.expressionsAccessTicks += elapsedTicks;
        }

        public void AddExpressionsCount(long elapsedTicks)
        {
            this.ExpressionsCountReads++;
            this.expressionsCountTicks += elapsedTicks;
        }

        public void AddExpressionsEnumeration(long elapsedTicks)
        {
            this.ExpressionsEnumerationCalls++;
            this.expressionsEnumerationTicks += elapsedTicks;
        }

        public SnapshotRootTiming AddRootTiming(string sectionName, string rootName, long scalarTicks)
        {
            var timing = new SnapshotRootTiming(sectionName, rootName, scalarTicks);
            this.rootTimings.Add(timing);
            return timing;
        }

        public void AddNameRead(long elapsedTicks)
        {
            this.NameReads++;
            this.nameTicks += elapsedTicks;
        }

        public void AddValueRead(long elapsedTicks)
        {
            this.ValueReads++;
            this.valueTicks += elapsedTicks;
        }

        public void AddTypeRead(long elapsedTicks)
        {
            this.TypeReads++;
            this.typeTicks += elapsedTicks;
        }

        public string ToLogDetail()
        {
            return "localsRoots=" + this.LocalRootCount +
                "; autosRoots=" + this.AutoRootCount +
                "; capturedNodes=" + this.CapturedNodes +
                "; skippedNodes=" + this.SkippedNodes +
                "; maxDepthHits=" + this.MaxDepthHits +
                "; perRootBudgetHits=" + this.PerRootBudgetHits +
                "; timeBudgetHits=" + this.TimeBudgetHits +
                "; hitExtractionCutoff=" + this.HitExtractionCutoff +
                "; dataMembersCalls=" + this.DataMembersCalls +
                "; dataMembersMs=" + ToMilliseconds(this.dataMembersTicks) +
                "; nameReads=" + this.NameReads +
                "; nameMs=" + ToMilliseconds(this.nameTicks) +
                "; valueReads=" + this.ValueReads +
                "; valueMs=" + ToMilliseconds(this.valueTicks) +
                "; typeReads=" + this.TypeReads +
                "; typeMs=" + ToMilliseconds(this.typeTicks) +
                "; expressionsAccessCalls=" + this.ExpressionsAccessCalls +
                "; expressionsAccessMs=" + ToMilliseconds(this.expressionsAccessTicks) +
                "; expressionsCountReads=" + this.ExpressionsCountReads +
                "; expressionsCountMs=" + ToMilliseconds(this.expressionsCountTicks) +
                "; expressionsEnumerationCalls=" + this.ExpressionsEnumerationCalls +
                "; expressionsEnumerationMs=" + ToMilliseconds(this.expressionsEnumerationTicks) +
                "; dataMembersCountReads=" + this.DataMembersCountReads +
                "; dataMembersCountMs=" + ToMilliseconds(this.dataMembersCountTicks) +
                "; dataMembersEnumerationCalls=" + this.DataMembersEnumerationCalls +
                "; dataMembersEnumerationMs=" + ToMilliseconds(this.dataMembersEnumerationTicks);
        }

        public string ToRootTimingLogDetail()
        {
            if (this.rootTimings.Count == 0)
            {
                return "topSlowRoots=none";
            }

            var timings = new List<SnapshotRootTiming>(this.rootTimings);
            timings.Sort((left, right) => right.TotalTicks.CompareTo(left.TotalTicks));

            var builder = new StringBuilder("topSlowRoots=");
            var count = Math.Min(5, timings.Count);
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(" | ");
                }

                var timing = timings[index];
                builder.Append(timing.SectionName);
                builder.Append('/');
                builder.Append(timing.RootName);
                builder.Append(" totalMs=");
                builder.Append(ToMilliseconds(timing.TotalTicks));
                builder.Append(" scalarMs=");
                builder.Append(ToMilliseconds(timing.ScalarTicks));
                builder.Append(" expandMs=");
                builder.Append(ToMilliseconds(timing.ExpansionTicks));
            }

            return builder.ToString();
        }

        public string ToAccountedTimeLogDetail(long totalExtractionTicks)
        {
            var accountedTicks = Math.Min(this.AccountedTicks, totalExtractionTicks);
            var unaccountedTicks = Math.Max(0, totalExtractionTicks - accountedTicks);
            return "totalExtractionMs=" + ToMilliseconds(totalExtractionTicks) +
                "; accountedMs=" + ToMilliseconds(accountedTicks) +
                "; unaccountedMs=" + ToMilliseconds(unaccountedTicks) +
                "; accountedPercent=" + ToPercent(accountedTicks, totalExtractionTicks) +
                "; unaccountedPercent=" + ToPercent(unaccountedTicks, totalExtractionTicks);
        }

        private static string ToMilliseconds(long elapsedTicks)
        {
            var milliseconds = elapsedTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;
            return milliseconds.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string ToPercent(long valueTicks, long totalTicks)
        {
            if (totalTicks <= 0)
            {
                return "0";
            }

            var percent = valueTicks * 100d / totalTicks;
            return percent.ToString("0.#", CultureInfo.InvariantCulture);
        }

        internal sealed class SnapshotRootTiming
        {
            public SnapshotRootTiming(string sectionName, string rootName, long scalarTicks)
            {
                this.SectionName = sectionName;
                this.RootName = string.IsNullOrWhiteSpace(rootName) ? "<unnamed>" : rootName;
                this.ScalarTicks = scalarTicks;
            }

            public string SectionName { get; }

            public string RootName { get; }

            public long ScalarTicks { get; }

            public long ExpansionTicks { get; private set; }

            public long TotalTicks => this.ScalarTicks + this.ExpansionTicks;

            public void AddExpansion(long elapsedTicks)
            {
                this.ExpansionTicks += elapsedTicks;
            }
        }
    }
}
