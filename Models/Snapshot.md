# Snapshot Model Reference

This document describes the data model used by Debug Capture to represent a single debugger snapshot (a `Breakpoint`, `Step`, `StepIn`, `StepOver`, or `Exception` capture moment). These classes live in [`Models/Snapshot.cs`](Snapshot.cs) and are designed to be serialized to a structured file format (e.g. JSON) alongside the paired screenshot.

## Overview

```
Snapshot
├── SchemaVersion        (int)
├── Trigger              (SnapshotTrigger)
├── Timestamp            (DateTimeOffset)
├── Filename             (string)
├── FilePath             (string)
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
| `SchemaVersion` | `int` | Version number of the snapshot file format. Defaults to `1`. Allows future consumers (tools, viewers, AI agents) to detect and handle older/newer snapshot shapes without guessing. Increment this whenever a breaking change is made to the model. |
| `Trigger` | `SnapshotTrigger` | What caused this snapshot to be captured — a breakpoint hit, a step action, or an exception. See [`SnapshotTrigger`](#snapshottrigger-enum) below. |
| `Timestamp` | `DateTimeOffset` | The moment the snapshot was captured, including local offset information. Used to build the shared filename prefix that pairs the screenshot and snapshot file together and to preserve time-zone context when snapshots are shared. |
| `Filename` | `string` | Name of the source file the debugger was stopped in (e.g. `Program.cs`). |
| `FilePath` | `string` | Path to the source file, relative to the containing project when possible (falls back to the full path otherwise). |
| `LineNumber` | `int` | The line number in the source file where execution was stopped. |
| `LineText` | `string` | A copy of the literal source text on that line, so a snapshot can be understood without re-opening the original file (useful if the file changes later, or the snapshot is viewed on a different machine). |
| `Locals` | `List<SnapshotProperty>` | Variables from the debugger's **Locals** window at the time of capture. |
| `Autos` | `List<SnapshotProperty>` | Variables from the debugger's **Autos** window (arguments and recently used expressions). |
| `Watch1`–`Watch4` | `List<SnapshotProperty>` | Snapshots of up to four Watch windows. *(Note: not yet populated by the export service — reserved for future implementation.)* |
| `Exception` | `SnapshotException` | Populated only when `Trigger == SnapshotTrigger.Exception`. Contains exception details (type, message, stack trace, members). See [`SnapshotException`](#snapshotexception) below. |
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
| `Value` | `string` | The evaluated value as a string. If evaluation fails, this may be empty or contain the partial value available from the debugger. The structured error message should be stored in `EvaluationError`. |
| `Type` | `string` | The declared or runtime type of the variable/member (e.g. `int`, `User`, `System.String`). |
| `EvaluationError` | `string` | Error message captured when Visual Studio could not evaluate this property. Separating this from `Value` lets a UI show failed evaluations without parsing text such as `<error: ...>`. |
| `HasEvaluationError` | `bool` | Computed property that returns `true` when `EvaluationError` is not null, empty, or whitespace. Useful for UI binding and filtering. |
| `Children` | `List<SnapshotProperty>` | Optional. Populated only when this property represents an expandable/complex object whose members were also captured (e.g. nested object fields). `null` for simple/scalar values. |

---

## `SnapshotException`

Details about an exception, populated when `Snapshot.Trigger` is `Exception`. Some fields are CLR/.NET-specific and may be empty when debugging non-.NET targets (native C++, Python, etc.) — see notes below.

| Property | Type | Description |
|---|---|---|
| `TypeName` | `string` | Fully qualified exception type name (e.g. `System.NullReferenceException`). Universal across debugger types. |
| `Message` | `string` | The exception's message text. Universal across debugger types. |
| `Source` | `string` | **CLR-specific** (`Exception.Source`). May be empty for non-.NET debug targets. |
| `TargetSite` | `string` | **CLR-specific** (`Exception.TargetSite`, a reflection concept). May be empty for non-.NET debug targets. |
| `HResult` | `string` | Native (Win32/COM) or CLR HRESULT value, when available. Exists in both managed and native contexts, though its meaning/format differs. |
| `StackTrace` | `string` | Raw stack trace text. Format varies by language/runtime (.NET managed frames vs. native call stack vs. other runtimes), but the field itself is universal. |
| `InnerException` | `SnapshotException` | **CLR-specific** recursive reference to a wrapped/chained exception (`Exception.InnerException`). `null` when there is no inner exception or the concept doesn't apply to the debugged language. |
| `MembersTruncated` | `bool` | `true` when the `Members` list was cut off due to depth or count limits during capture (see `ExceptionMemberDepthLimit` / `ExceptionMemberCountLimit` in `DebuggerVariableExportService`), so consumers know the list isn't necessarily exhaustive. |
| `Members` | `List<SnapshotProperty>` | Flattened/recursive dump of the exception object's data members (walks `Expression.DataMembers` via the debugger's expression evaluator), each entry potentially having its own `Children`. |

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

- **Fault tolerance**: Fields sourced from live debugger expression evaluation (`Value`, stack trace details, etc.) should capture failures in `EvaluationError` rather than throwing, consistent with the extension's existing fault-tolerant capture philosophy (see README's "Variable reads are fault tolerant" section).
- **Cross-language debugging**: Because captures go through Visual Studio's language-agnostic `EnvDTE`/`Debugger` COM API, most of this model works for any debugger-supported language (C++, Python, etc.), with the exception of a few CLR-only `SnapshotException` fields noted above, which will simply be empty rather than causing failures.
- **Extensibility**: `SchemaVersion` and the optional/nullable nature of fields like `SnapshotProperty.Children` and `SnapshotCallStackFrame.Line` are intended to let the format evolve without breaking older snapshot files or downstream tooling.
- **Not yet implemented**: `Watch1`–`Watch4` are reserved in the model but not currently populated by `DebuggerVariableExportService`. A future revision may replace these fixed properties with a dynamic `List<SnapshotWatchWindow>` to support an arbitrary number of named Watch windows.
