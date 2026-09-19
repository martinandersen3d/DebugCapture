# Property Extraction Solution Plan

## Goal

Design a debugger property extraction strategy that captures enough information to explain a breakpoint, step, or exception without blocking Visual Studio, evaluating dangerous object graphs, or producing oversized JSON files.

Primary constraints:

- Must work across multiple programming languages supported by Visual Studio debugging, not only C#/.NET.
- Must prioritize performance and avoid blocking the IDE/debugger UI.
- Must keep implementation complexity controlled.
- Must use clear business rules so snapshots stay compact without relying on a hard JSON file-size target.
- Must make incomplete data explicit so humans and AI agents know when a snapshot is partial.
- Must keep extraction best-effort and fault-tolerant.
- Must preserve async/non-blocking behavior wherever Visual Studio's debugger API allows it.
- Must avoid expanding whole exception objects during exception captures.

## Vision

Debug Capture should create bounded debugger observations, not full serialized program state.

The snapshot should be useful enough to understand the debugging moment while remaining fast, predictable, and safe to capture automatically. The implementation should favor simple cross-language budgets over smart debugger interpretation. This keeps the code maintainable and avoids expensive debugger evaluation on the Visual Studio main thread.

## Capture Policy Source of Truth

These business rules are the source of truth for the first implementation. Other sections should describe how to apply these rules, not introduce conflicting defaults.

| Area | Default |
|---|---:|
| Max object depth | 3 |
| Max children per object | 100 |
| Max normal string/value length | 10,000 chars |
| Normal extraction target | 250-500 ms |
| Hard extraction cutoff | 1,500 ms |
| Max total property nodes | 2,000 |
| Exception member expansion | Disabled by default |
| Root locals | Capture visible roots until global budget is hit |
| Root autos | Capture visible roots until global budget is hit |
| Overlapping captures | Skip while busy; never queue unlimited captures |

The key principle is:

> Root-level capture should be broad; nested capture should be strictly bounded and explicit about what was omitted. Exception capture should be summary-first and should not expand the whole exception object.

The first production pass should focus on five core features:

| # | Feature | Recommendation | Cross-Language Confidence | Implementation Confidence | Performance Confidence | Priority |
|---:|---|---|---:|---:|---:|---|
| 1 | Capture backpressure / single-flight capture | Prevent unlimited overlapping captures; skip while busy. | 0.95 | 0.90 | 0.95 | Phase 0 |
| 2 | Extraction time budget | Stop main-thread debugger extraction when the time budget is exceeded. | 0.95 | 0.85 | 0.95 | Phase 1 |
| 3 | Bounded object graph expansion with child counts | Enforce max depth, max children, max total nodes, and record `ChildrenTotalCount` / `ChildrenSnapshotCount`. | 0.93 | 0.92 | 0.95 | Phase 1 |
| 4 | String/value business rules | Shorten large values using the capture policy limit. | 0.95 | 0.95 | 0.95 | Phase 1 |
| 5 | Summary-first exception capture | Capture useful exception fields without expanding the whole exception object graph. | 0.85 | 0.90 | 0.95 | Phase 1 |

## Current State

The current implementation uses Visual Studio's language-agnostic `EnvDTE` debugger object model:

- `StackFrame.Locals`
- `StackFrame.Arguments`
- `Expression.Value`
- `Expression.Type`
- `Expression.DataMembers`

This is good for cross-language support because Visual Studio debugger integrations for C#, C++, Python, JavaScript, and other languages can expose values through the same DTE abstraction.

Current limits:

- `MemberDepthLimit = 3`
- `MemberCountLimit = 100`

Current async/threading shape:

- `DebuggerBreakpointCaptureListener` queues capture work with `JoinableTaskFactory.RunAsync`.
- `DebuggerVariableExportService.BuildSnapshotAsync` switches to the Visual Studio main thread before reading DTE/debugger values.
- JSON serialization happens after extraction and before file writing.
- File writing uses async `FileStream` and is off the main thread.
- Screenshot capture runs separately from JSON export.
- The snapshot browser loads snapshot files asynchronously with `Task.Run`.

Current gaps:

- DTE/debugger value extraction happens on the main thread, so extraction must be short and bounded.
- Multiple rapid breakpoints/steps can queue overlapping captures; without backpressure this can create UI lag.
- Normal `SnapshotProperty` nodes do not say how many children were visible versus captured.
- The global counter does not communicate whether the snapshot as a whole was partial.
- A node does not say how many children existed at snapshot time versus how many children were included in the JSON.
- Huge strings are not truncated.
- Exception captures can attempt to expand the exception object's members instead of staying summary-first.

## Design Principles

### Keep the Current Cross-Language DTE Foundation

The first production implementation should stay based on `EnvDTE.Expression` rather than switching to .NET-specific debugging APIs such as DKM or CLR-only APIs.

Reasons:

- `EnvDTE` is already used by the extension.
- The README and model documentation position the feature as lightweight and debugger-language-neutral.
- It is the most practical cross-language API currently in the codebase.
- A DKM-based extractor may offer more control for .NET, but it would reduce cross-language confidence and significantly increase complexity.

### Keep DTE Access on the Main Thread

`EnvDTE` objects are COM-based Visual Studio automation objects. Reading `Debugger`, `StackFrame`, `Expression`, `DataMembers`, and `ActiveDocument` should be treated as main-thread work.

Therefore:

- Do **not** move `EnvDTE` expression traversal to `Task.Run`.
- Do **not** access `Expression.DataMembers` on a background thread.
- Keep the main-thread DTE section extremely small and budgeted.
- Move only pure CPU/string/serialization/file work off the main thread.

Performance must primarily come from **doing less debugger evaluation**, not from parallelizing debugger evaluation.

### Prefer Business Rules Over Smart Interpretation

The first pass should avoid complex debugger interpretation. It should not try to deeply understand collections, dictionaries, object identity, or runtime-specific exception internals.

Use simple business rules that are reliable across languages:

- time budget,
- depth budget,
- per-object child budget,
- total node budget,
- string/value length budget,
- explicit child-count metadata,
- summary-first exception capture.

This keeps code complexity controlled while still addressing the biggest performance risks.

## Suggested Model Changes

Keep model changes small and directly tied to performance, non-blocking behavior, and snapshot clarity.

### `SnapshotProperty`

Recommended additions:

```csharp
public class SnapshotProperty
{
    public string Name { get; set; }

    public string Value { get; set; }

    public string Type { get; set; }

    public int? ChildrenTotalCount { get; set; }

    public int? ChildrenSnapshotCount { get; set; }

    public List<SnapshotProperty> Children { get; set; }
}
```

Notes:

- `ChildrenTotalCount` is the number of debugger children visible at snapshot time, when this can be read cheaply from `Expression.DataMembers.Count`.
- `ChildrenSnapshotCount` is the number of child nodes included in the JSON snapshot.
- If `ChildrenTotalCount` is greater than `ChildrenSnapshotCount`, consumers can tell the child list is partial without a separate per-property truncation flag.
- Keep `Children` nullable for compact JSON output.
- Use the same generic `SnapshotProperty` shape for objects, lists, arrays, and dictionaries in the first pass.

## Feature Recommendations

### 1. Capture Backpressure / Single-Flight Capture

Add a non-blocking guard to prevent unlimited overlapping captures.

Recommended behavior:

- If a capture is active, skip the next capture.
- Prefer skipping over blocking.
- Log a lightweight message when captures are skipped.
- Do not queue an unlimited backlog while the user is rapidly stepping.

Reason: This is independent of debugger language and directly protects Visual Studio responsiveness with small implementation complexity.

### 2. Extraction Time Budget

Add a stopwatch-based time budget to prevent the main thread from being held too long.

Recommended behavior:

- Check time before accessing `DataMembers`.
- Check time before every child expansion.
- When exceeded, stop expanding and return the partial snapshot collected so far.
- Still write a partial snapshot.

Reason: This works regardless of language and directly addresses UI responsiveness.

### 3. Bounded Object Graph Expansion With Child Counts

Combine the current depth/count logic into one explicit extraction budget and make child completeness visible.

Recommended behavior:

- Keep max depth at `3` by default.
- Limit children per object to `100` by default.
- Limit total captured property nodes to `2,000` by default.
- Check depth before accessing `DataMembers` to avoid unnecessary debugger work.
- Set `ChildrenTotalCount` from `Expression.DataMembers.Count` when available and cheap.
- Set `ChildrenSnapshotCount` to the number of child nodes actually included in the snapshot.
- If `ChildrenSnapshotCount < ChildrenTotalCount`, the child list is known to be partial.
- Do not add per-property truncation flags; use the two child count fields to show partial child lists.

Example JSON:

```json
{
  "Name": "Customer",
  "Type": "Customer",
  "Value": "{Customer}",
  "ChildrenTotalCount": 135,
  "ChildrenSnapshotCount": 100
}
```

Reason: Depth, child, and total-node limits are simple, language-neutral, and prevent dangerous object graph traversal without requiring smart collection or identity detection. The two count fields make partial child lists explicit without large schema changes.

### 4. String/Value Business Rules

Limit large values during extraction using the capture policy source of truth.

Recommended behavior:

- Truncate normal string/value output at `10,000` chars by default.
- Apply the same string/value limit to every debugger value string.
- Make shortened values obvious in the value text itself, for example by appending `...`.
- Keep serialization simple: serialize the already-budgeted snapshot once.

Example JSON:

```json
{
  "Name": "responseBody",
  "Type": "string",
  "Value": "first 10000 chars..."
}
```

Reason: String/value truncation is language-independent and keeps output bounded without adding retry serialization or debugger-specific complexity.

### 5. Summary-First Exception Capture

Exception snapshots should capture useful exception information without expanding the whole exception object graph.

Recommended behavior:

- Keep dedicated `SnapshotException` fields as the primary exception representation.
- Capture type, message, source, target site, HResult, and stack trace as best-effort scalar fields.
- Apply string/value business rules to exception message and stack trace.
- Do not recursively expand all `$exception.DataMembers` by default.
- If member capture is kept, use a very small exception member budget and rely on `MembersTruncated` to show that members were omitted.
- Treat .NET-specific exception expressions as best-effort and do not let failures block the snapshot.

Example JSON intent:

```json
{
  "Exception": {
    "TypeName": "System.InvalidOperationException",
    "Message": "Operation failed...",
    "MembersTruncated": true
  }
}
```

Reason: Exceptions are often large runtime objects. A summary-first policy keeps exception captures useful while avoiding deep runtime graph expansion and UI-thread delays.

## Recommended Implementation Phases

### Phase 0 — Capture Pipeline Backpressure

Implement before deeper extraction work:

1. Add a single-flight guard in `DebuggerBreakpointCaptureListener`.
2. Skip captures if one is already running.
3. Keep the debugger responsive over capture completeness.
4. Add lightweight stats/logging for skipped captures.

Expected impact:

- Prevents capture backlog during rapid stepping.
- Reduces crash/hang risk.

### Phase 1 — Safe Bounded Extraction

Implement the main extraction safety work:

1. Add `ChildrenTotalCount` and `ChildrenSnapshotCount` to `SnapshotProperty`.
2. Replace the current `SnapshotMemberCounter` with a `SnapshotExtractionBudget` object.
3. Enforce:
   - max extraction time,
   - max depth,
   - max children per node,
   - max total nodes,
   - max string/value length.
4. Make partial child captures explicit through child counts.
5. Make exception capture summary-first and avoid whole exception object expansion.
6. Move property walking into a dedicated extraction service.

Expected impact:

- Large performance improvement.
- Non-blocking behavior is much more predictable.
- Code remains budget-driven instead of heuristic-heavy.
- Strong cross-language compatibility.

## Proposed Internal Types

### `SnapshotExtractionOptions`

```csharp
internal sealed class SnapshotExtractionOptions
{
    public int MaxDepth { get; set; } = 3;

    public int MaxChildrenPerNode { get; set; } = 100;

    public int MaxTotalNodes { get; set; } = 2000;

    public int MaxValueLength { get; set; } = 10000;

    public int MaxExtractionMilliseconds { get; set; } = 1500;

    public int MaxExceptionMembers { get; set; } = 0;
}
```

### `SnapshotExtractionBudget`

```csharp
internal sealed class SnapshotExtractionBudget
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    public SnapshotExtractionOptions Options { get; }

    public int CapturedNodes { get; private set; }

    public bool IsNodeBudgetExhausted => this.CapturedNodes >= this.Options.MaxTotalNodes;

    public bool IsTimeBudgetExhausted => this.stopwatch.ElapsedMilliseconds >= this.Options.MaxExtractionMilliseconds;

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
```

### `SnapshotPropertyExtractionResult`

```csharp
internal sealed class SnapshotPropertyExtractionResult
{
    public List<SnapshotProperty> Properties { get; set; } = new();

    public int CapturedNodeCount { get; set; }

    public int ExtractionMilliseconds { get; set; }
}
```

## Async and Non-Blocking Implementation Rules

1. DTE/debugger reads stay on the main thread.
2. DTE/debugger reads must be bounded by time, node, depth, child, and string/value budgets.
3. Do not use `Task.Run` around `EnvDTE` objects.
4. Serialization and file IO should happen off the main thread after the snapshot object is already budgeted.
5. Capture should be single-flight; do not allow unbounded queued captures.
6. Exception capture should be summary-first and must not expand the whole exception object by default.
7. UI refresh should use background dispatch and cancellation.

## Cross-Language Compatibility Notes

The selected features are language-neutral or conservative enough to work broadly:

- capture backpressure,
- extraction time budget,
- bounded depth/child/node expansion with child counts,
- string/value business rules,
- summary-first exception capture.

The first implementation should be conservative and should never depend on C#-only assumptions.

## Recommended First Work Item

Start with **Phase 0** and **Phase 1**.

Phase 0 protects the debugger from capture backlog.

Phase 1 gives the highest safety/performance return and has the best cross-language confidence.

Minimum acceptance criteria:

- Rapid stepping does not queue unlimited captures.
- A pathological object graph does not freeze Visual Studio.
- Main-thread extraction is bounded by time and node limits.
- Large debugger value strings are shortened by clear business rules.
- Child snapshots report both total visible children and included snapshot children when possible.
- Exception captures do not expand the whole exception object by default.
- Partial child captures are explicit in JSON through `ChildrenTotalCount` and `ChildrenSnapshotCount`.
- Existing Locals/Autos behavior still works for simple snapshots.
- C#, C++, Python, or any other Visual Studio-supported language can still produce usable root variable snapshots through `EnvDTE`.
