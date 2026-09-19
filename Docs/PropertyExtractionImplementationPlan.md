# Property Extraction Implementation Plan

## Goal

Implement the approved safe property extraction design from `PropertyExtractionSolution.md` with minimal code complexity and strong protection for Visual Studio responsiveness.

## Scope

This implementation covers the first production pass:

1. Capture backpressure / single-flight capture.
2. Extraction time budget.
3. Bounded object graph expansion with child counts.
4. String/value truncation business rules.
5. Summary-first exception capture.

Out of scope for this pass:

- Hard JSON file-size enforcement.
- Snapshot browser index loading changes.
- Debugger-runtime-specific collection or dictionary interpretation.
- DKM/CLR-specific extraction APIs.
- Snapshot-level capture statistics metadata.

## Source-of-Truth Defaults

| Rule | Default |
|---|---:|
| Max object depth | 3 |
| Max children per object | 100 |
| Max normal string/value length | 10,000 chars |
| Hard extraction cutoff | 1,500 ms |
| Max total property nodes | 2,000 |
| Exception member expansion | Disabled by default |
| Overlapping captures | Skip while busy |

## Implementation Steps

### 1. Capture Backpressure

Update `DebuggerBreakpointCaptureListener` so only one capture can run at a time.

- Use a non-blocking single-flight guard.
- Skip new captures while a capture is active.
- Log a lightweight skip message.
- Always release the guard in `finally`.

### 2. Snapshot Model Additions

Update `SnapshotProperty` with:

- `ChildrenTotalCount`
- `ChildrenSnapshotCount`

These fields make partial child captures visible without adding per-property truncation flags.

### 3. Extraction Budget Types

Replace the current simple member counter with budget-driven extraction state:

- `SnapshotExtractionOptions`
- `SnapshotExtractionBudget`

The budget enforces:

- max depth,
- max children per node,
- max total property nodes,
- max extraction milliseconds,
- max value length.

### 4. Bounded DTE Traversal

Refactor property traversal so all `EnvDTE.Expression` reads remain on the main thread but are guarded by budget checks.

Rules:

- Check the budget before reading `DataMembers`.
- Check depth before reading `DataMembers`.
- Capture roots until the global budget is exhausted.
- Capture at most 100 children per node.
- Set `ChildrenTotalCount` when `DataMembers.Count` is available.
- Set `ChildrenSnapshotCount` when child nodes are included.
- Keep `Children` null when no children are included.

### 5. String/Value Truncation

Apply the max value length to debugger strings during extraction.

- Truncate values over 10,000 characters.
- Append `...` to make shortening obvious.
- Use the same rule for normal values and exception scalar fields.

### 6. Summary-First Exception Capture

Keep exception capture focused on the fastest useful scalar fields.

- Capture exception message and stack trace by default.
- Continue capturing normal locals/autos during exception-triggered captures.
- Keep `$exception` and exception-typed local values scalar-only by default.
- Do not expand `$exception.DataMembers` by default.
- Leave exception member expansion disabled unless options are explicitly changed later.

### 7. Validation

Build the solution after implementation and fix compile errors caused by these changes.
