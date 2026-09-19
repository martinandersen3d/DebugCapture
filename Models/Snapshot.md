# Snapshot Model Reference

This document describes the data model used by Debug Capture to represent a single debugger snapshot (a `Breakpoint`, `Step`, `StepIn`, `StepOver`, or `Exception` capture moment). These classes live in [`Models/Snapshot.cs`](Snapshot.cs) and are designed to be serialized to a structured file format (e.g. JSON) alongside the paired screenshot.

## Overview

```
Snapshot
├── $description        (string)
├── SchemaVersion        (int)
├── Trigger              (SnapshotTrigger)
├── Timestamp            (DateTimeOffset)
├── FileName             (string)
├── Folder               (string)
├── LineNumber           (int)
├── LineText             (string)
├── Locals               (List<SnapshotProperty>)
├── Autos                (List<SnapshotProperty>)
├── Watch1..Watch4       (List<SnapshotProperty>)
├── Exception            (SnapshotException)
├── CallStack            (List<SnapshotCallStackFrame>)
├── Info                 (SnapshotInfo)
├── ImageFilePath        (string)
└── SnapshotFilePath     (string)
```

---

## `Snapshot`

The root object representing everything captured at a single debugger stop.

| Property | Type | Description |
|---|---|---|
| `$description` | `string` | Short, fixed description of what this JSON file represents, serialized as the topmost key so an AI agent or unfamiliar reader immediately understands the file's context without prior knowledge of the schema. |
| `SchemaVersion` | `int` | Version number of the snapshot file format. Defaults to `1`. Allows future consumers (tools, viewers, AI agents) to detect and handle older/newer snapshot shapes without guessing. Increment this whenever a breaking change is made to the model. |
| `Trigger` | `SnapshotTrigger` | What caused this snapshot to be captured — a breakpoint hit, a step action, or an exception. See [`SnapshotTrigger`](#snapshottrigger-enum) below. |
| `Timestamp` | `DateTimeOffset` | The moment the snapshot was captured, including local offset information. Used to build the shared filename prefix that pairs the screenshot and snapshot file together and to preserve time-zone context when snapshots are shared. |
| `FileName` | `string` | Name of the source file the debugger was stopped in (e.g. `Program.cs`). |
| `Folder` | `string` | Full path to the folder containing the source file. |
| `LineNumber` | `int` | The line number in the source file where execution was stopped. |
| `LineText` | `string` | A copy of the literal source text on that line, so a snapshot can be understood without re-opening the original file (useful if the file changes later, or the snapshot is viewed on a different machine). |
| `Locals` | `List<SnapshotProperty>` | Variables from the debugger's **Locals** window at the time of capture. |
| `Autos` | `List<SnapshotProperty>` | Variables from the debugger's **Autos** window (arguments and recently used expressions). |
| `Watch1`–`Watch4` | `List<SnapshotProperty>` | Snapshots of up to four Watch windows. *(Note: not yet populated by the export service — reserved for future implementation.)* |
| `Exception` | `SnapshotException` | Populated only when `Trigger == SnapshotTrigger.Exception`. Contains a performance-first exception summary, primarily message and stack trace. See [`SnapshotException`](#snapshotexception) below. |
| `CallStack` | `List<SnapshotCallStackFrame>` | The full call stack at the time of capture, ordered from innermost (current) frame outward. |
| `Info` | `SnapshotInfo` | Session/environment context (project, solution, process, thread). See [`SnapshotInfo`](#snapshotinfo) below. |
| `ImageFilePath` | `string` | Full path to the paired screenshot (`.png`) file for this snapshot, so a viewer/tool doesn't need to infer pairing purely from filename convention. |
| `SnapshotFilePath` | `string` | Full path to this snapshot file itself (self-reference), useful once the object is loaded independently of its original file location. |

---

## `SnapshotInfo`

Session/environment context for the capture. Deliberately **excludes machine identity** (no `MachineName`) to avoid leaking local environment details into shared/exported snapshots.

| Property | Type | Description |
|---|---|---|
| `ProjectName` | `string` | Name of the Visual Studio project containing the active document, when resolvable. |
| `SolutionName` | `string` | Name of the currently open solution. |
| `ProcessName` | `string` | Name of the process being debugged (`dte.Debugger.CurrentProcess`). |
| `ThreadName` | `string` | Name/identifier of the thread that was active when the snapshot was captured (`dte.Debugger.CurrentThread`). |

---

## `SnapshotProperty`

A single name/value/type entry, used for Locals, Autos, Watches, and exception members. This is the primary "columnar" building block of the model — a flat table can be rendered directly from a list of these.

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | The variable or member name (e.g. `i`, `user`, `Message`). |
| `Value` | `string` | The evaluated value as a string. If evaluation fails, this may contain an `<error: ...>` placeholder rather than throwing, keeping capture fault-tolerant. |
| `Type` | `string` | The declared or runtime type of the variable/member (e.g. `int`, `User`, `System.String`). |
| `ChildrenTotalCount` | `int?` | Optional. Number of debugger children visible at snapshot time, when this can be read from `Expression.DataMembers.Count` within the extraction budget. |
| `ChildrenSnapshotCount` | `int?` | Optional. Number of child nodes included in the JSON snapshot. If this is lower than `ChildrenTotalCount`, the child list is partial. |
| `Children` | `List<SnapshotProperty>` | Optional. Populated only when this property represents an expandable/complex object whose members were also captured (e.g. nested object fields). `null` for simple/scalar values. |

---

## `SnapshotException`

Details about an exception, populated when `Snapshot.Trigger` is `Exception`. Exception capture prioritizes performance and avoids expanding the exception object or normal locals/autos by default.

| Property | Type | Description |
|---|---|---|
| `TypeName` | `string` | Usually empty by default. Reserved for future/enhanced exception capture. |
| `Message` | `string` | The exception's message text. Captured by default. |
| `Source` | `string` | Usually empty by default. Reserved for future/enhanced exception capture. |
| `TargetSite` | `string` | Usually empty by default. Reserved for future/enhanced exception capture. |
| `HResult` | `string` | Usually empty by default. Reserved for future/enhanced exception capture. |
| `StackTrace` | `string` | Raw stack trace text. Captured by default when available. |
| `InnerException` | `SnapshotException` | **CLR-specific** recursive reference to a wrapped/chained exception (`Exception.InnerException`). `null` when there is no inner exception or the concept doesn't apply to the debugged language. |
| `MembersTruncated` | `bool` | `true` when exception member expansion was omitted or cut off by the extraction budget. Exception member expansion is disabled by default. |
| `Members` | `List<SnapshotProperty>` | Optional exception members. Empty by default because exception capture is summary-first and does not expand the whole exception object graph. |

---

## `SnapshotCallStackFrame`

A single frame in the captured call stack.

| Property | Type | Description |
|---|---|---|
| `FunctionName` | `string` | The function/method name for this frame. |
| `Module` | `string` | The module (assembly/binary) the function belongs to. |
| `File` | `string` | Source file for this frame, when resolvable by the debugger engine. |
| `Line` | `int?` | Line number within `File` for this frame, when resolvable. `null` when unavailable (e.g. no symbols, external code). |
| `IsCurrentFrame` | `bool` | `true` if this frame is the active/current frame (i.e., matches the debugger's current stack frame at the time of capture). |

---

## `SnapshotTrigger` enum

Identifies what caused the capture to happen.

| Value | Meaning |
|---|---|
| `Breakpoint` | A breakpoint was hit. |
| `Step` | A generic step action occurred. |
| `StepIn` | The user performed **Step Into**. |
| `StepOver` | The user performed **Step Over**. |
| `Exception` | An exception was thrown or went unhandled while the exception helper window was shown. |

### `SnapshotTriggerExtensions.GetFileSuffix(this SnapshotTrigger trigger)`

Maps each `SnapshotTrigger` value to the uppercase filename suffix used when naming capture files, matching the convention described in the project [README](../README.md):

| Trigger | Suffix |
|---|---|
| `Breakpoint` | `BREAKPOINT` |
| `Step` | `STEP` |
| `StepIn` | `STEP-IN` |
| `StepOver` | `STEP-OVER` |
| `Exception` | `EXCEPTION` |

---

## Design notes

- **Fault tolerance**: Fields sourced from live debugger expression evaluation (`Value`, stack trace details, etc.) may contain an `<error: ...>` placeholder instead of throwing, consistent with the extension's existing fault-tolerant capture philosophy (see README's "Variable reads are fault tolerant" section).
- **Cross-language debugging**: Because captures go through Visual Studio's language-agnostic `EnvDTE`/`Debugger` COM API, most of this model works for any debugger-supported language (C++, Python, etc.), with the exception of a few CLR-only `SnapshotException` fields noted above, which will simply be empty rather than causing failures.
- **Extensibility**: `SchemaVersion` and the optional/nullable nature of fields like `SnapshotProperty.Children` and `SnapshotCallStackFrame.Line` are intended to let the format evolve without breaking older snapshot files or downstream tooling.
- **Not yet implemented**: `Watch1`–`Watch4` are reserved in the model but not currently populated by `DebuggerVariableExportService`. A future revision may replace these fixed properties with a dynamic `List<SnapshotWatchWindow>` to support an arbitrary number of named Watch windows.
