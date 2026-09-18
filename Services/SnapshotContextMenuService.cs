using DebugCapture.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace DebugCapture.Services;

internal sealed class SnapshotContextMenuService
{
    public ContextMenu CreateSnapshotContextMenu(SnapshotListItem item)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var snapshot = item.Snapshot;

        return CreateMenu(
            CreateCopyMenuItem("Copy Snapshot Summary", () => BuildSnapshotSummary(snapshot, item.SnapshotFilePath)),
            CreateCopyMenuItem("Copy Full Source Path", () => GetSourcePath(snapshot)),
            CreateCopyMenuItem("Copy Line Text", () => snapshot.LineText),
            CreateCopyMenuItem("Copy Snapshot JSON Path", () => item.SnapshotFilePath),
            CreateSeparator(),
            CreateMenuItem("Show Snapshot in Explorer", () => ShowInExplorer(item.SnapshotFilePath)));
    }

    public ContextMenu CreatePropertyContextMenu(SnapshotProperty property)
    {
        if (property is null)
        {
            throw new ArgumentNullException(nameof(property));
        }

        return CreateMenu(
            CreateCopyMenuItem("Copy Name", () => property.Name),
            CreateCopyMenuItem("Copy Value", () => property.Value),
            CreateCopyMenuItem("Copy Type", () => property.Type),
            CreateCopyMenuItem("Copy Name = Value", () => property.Name + " = " + property.Value),
            CreateCopyMenuItem("Copy as JSON", () => JsonConvert.SerializeObject(property, Formatting.Indented)));
    }

    public ContextMenu CreateExceptionContextMenu(Snapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        return CreateMenu(
            CreateCopyMenuItem("Copy Exception Summary", () => BuildExceptionSummary(snapshot)),
            CreateCopyMenuItem("Copy Stack Trace", () => snapshot.Exception?.StackTrace),
            CreateCopyMenuItem("Copy AI Debug Prompt", () => BuildAiDebugPrompt(snapshot)));
    }

    public ContextMenu CreateCallStackContextMenu(SnapshotCallStackFrame frame, IEnumerable<SnapshotCallStackFrame>? callStack)
    {
        if (frame is null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        return CreateMenu(
            CreateCopyMenuItem("Copy Frame", () => BuildCallStackFrame(frame)),
            CreateCopyMenuItem("Copy Call Stack", () => BuildCallStack(callStack)));
    }

    private static ContextMenu CreateMenu(params object[] items)
    {
        var menu = new ContextMenu();

        foreach (var item in items)
        {
            switch (item)
            {
                case MenuItem menuItem:
                    menu.Items.Add(menuItem);
                    break;
                case Separator separator:
                    menu.Items.Add(separator);
                    break;
            }
        }

        return menu;
    }

    private static MenuItem CreateCopyMenuItem(string header, Func<string?> textFactory)
    {
        return CreateMenuItem(header, () => CopyText(textFactory()));
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static Separator CreateSeparator()
    {
        return new Separator();
    }

    private static void CopyText(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }

    private static string GetSourcePath(Snapshot snapshot)
    {
        return Path.Combine(snapshot.Folder ?? string.Empty, snapshot.FileName ?? string.Empty);
    }

    private static string BuildSnapshotSummary(Snapshot snapshot, string snapshotFilePath)
    {
        var builder = new StringBuilder();

        builder.AppendLine(snapshot.Trigger + " snapshot at " + snapshot.FileName + ":L" + snapshot.LineNumber);
        builder.AppendLine("Line: " + snapshot.LineText);
        builder.AppendLine("Time: " + snapshot.Timestamp.ToString("O"));
        builder.AppendLine("Project: " + snapshot.Info?.ProjectName);
        builder.AppendLine("Process: " + snapshot.Info?.ProcessName);
        builder.AppendLine("Source: " + GetSourcePath(snapshot));
        builder.AppendLine("Snapshot: " + snapshotFilePath);
        builder.AppendLine("Screenshot: " + snapshot.ImageFilePath);

        return builder.ToString().TrimEnd();
    }

    private static string BuildExceptionSummary(Snapshot snapshot)
    {
        var exception = snapshot.Exception;
        if (exception is null)
        {
            return "No exception captured.";
        }

        var builder = new StringBuilder();

        builder.AppendLine(CleanDebuggerText(exception.TypeName) + ": " + CleanDebuggerText(exception.Message));
        builder.AppendLine("At " + snapshot.FileName + ":L" + snapshot.LineNumber);
        builder.AppendLine("Line: " + snapshot.LineText);
        builder.AppendLine("HResult: " + exception.HResult);

        return builder.ToString().TrimEnd();
    }

    private static string BuildAiDebugPrompt(Snapshot snapshot)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Analyze this Visual Studio debugger snapshot.");
        builder.AppendLine();
        builder.AppendLine("Source:");
        builder.AppendLine(snapshot.FileName + ":L" + snapshot.LineNumber);
        builder.AppendLine(snapshot.LineText);
        builder.AppendLine();

        if (snapshot.Exception is not null)
        {
            builder.AppendLine("Exception:");
            builder.AppendLine(CleanDebuggerText(snapshot.Exception.TypeName) + ": " + CleanDebuggerText(snapshot.Exception.Message));
            builder.AppendLine();
        }

        builder.AppendLine("Locals:");
        foreach (var local in snapshot.Locals.Take(25))
        {
            builder.AppendLine(local.Name + " = " + local.Value + " (" + local.Type + ")");
        }

        builder.AppendLine();
        builder.AppendLine("Explain the likely cause and suggest a safe fix.");

        return builder.ToString().TrimEnd();
    }

    private static string BuildCallStackFrame(SnapshotCallStackFrame frame)
    {
        return frame.FunctionName + " at " + frame.File + ":" + frame.Line;
    }

    private static string BuildCallStack(IEnumerable<SnapshotCallStackFrame>? callStack)
    {
        if (callStack is null)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, callStack.Select(BuildCallStackFrame));
    }

    private static string CleanDebuggerText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"'
            ? text.Substring(1, text.Length - 2)
            : text;
    }

    private static void ShowInExplorer(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + filePath + "\"");
        }
    }
}
