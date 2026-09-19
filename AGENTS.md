Agent Task Prompt: Async Visual Studio Breakpoint Screenshot Extension

# Objective

Build a lightweight, highly responsive Visual Studio 2022+ extension (VSIX) that automatically captures a screenshot of the Visual Studio IDE window whenever a breakpoint is hit during a debugging session.

The extension must maintain a smooth, modern IDE experience by executing all capture and file storage tasks completely asynchronously off the main UI thread without blocking the editor.

Key Requirements & Architecture

## 1. Target Environment & Performance

Framework & Target: Target Visual Studio 2022 (64-bit VSSDK) using AsyncPackage.

Auto-Loading: Decorate the package with [ProvideAutoLoad(UIContextGuids80.Debugging, PackageAutoLoadFlags.BackgroundLoad)] so it only loads into memory upon entering a debug session, avoiding initial startup overhead.

Threading Rules: Adhere strictly to Visual Studio threading guidelines. Switch to the main UI thread via JoinableTaskFactory.SwitchToMainThreadAsync() only to query DTE/Debugger services and fetch window handles.

## 2. Event Interception & Debugger Behavior

Subscribe to DTE2.Events.DebuggerEvents.OnEnterBreakMode.

Verify that dbgExecutionReason == dbgExecutionReasonBreakpoint.

Ensure event handlers hold class-level strong references to prevent background Garbage Collection from silencing events.

Execution Flow: Allow Visual Studio to remain paused at the breakpoint as normal (do not force resume execution).

## 3. Non-Blocking Async Capture Pipeline

Capture Area: Capture only the Visual Studio IDE main window.

Obtain the Visual Studio main window handle (dte.MainWindow.HWnd or Win32 GetForegroundWindow()).

Retrieve the window bounds (GetWindowRect).

Perform the pixel capture using screen graphics or GDI interop.

Asynchronous Execution:

Immediately offload image capture, encoding, and disk operations to a background thread (Task.Run).

Ensure the main thread is released in under 10 milliseconds.

Async Storage: Write files using non-blocking streams (FileStream with useAsync: true and WriteAsync).

Default Directory: %USERPROFILE%\Pictures\VSScreenshots using the naming format VS_Capture_{yyyyMMdd_HHmmss_fff}.png.

4. UI Architecture & Future Extensibility

UI decoupler: Do not render any notifications or dialogs upon capture.

Modular Interface: Expose an internal messaging event or service interface (e.g., ICaptureNotificationService) so a custom UI component can be easily wired up later.

Error Handling & Reliability

Wrap capture routines in safe try/catch blocks to ensure failure never crashes the Visual Studio process or interrupts the user's debugging session.

Log internal errors silently to the Visual Studio Output Window (IVsOutputWindowPane) or a internal debug log stream.

# Coding style
- Always refactor code to maintain clarity and readability. and extract methods for repeated logic.
- avoid god classes and methods. Keep methods focused on a single responsibility.
- Use inspiration from Mads Kristensen's Visual Studio extension patterns and best practices, to guide your implementation of file structure.

---

# New requirements
- We need a FeatureFlag, that can toogle features on/off

Also take screenshot on: 
- breakpoint (like now) - also in featureflag
- Step-Over / Step-Into Sequences - also in featureflag
- Exceptions - also in featureflag

New feature:
-  screenshot Filename will be suffixed with allcaps, example: "-EXCEPTION", "-STEP-IN", "-BREAKPOINT" etc

---

# Current business rules

These rules describe the current implemented behavior and should guide future changes.

## Capture triggers and files

- Capture types are controlled by feature flags for breakpoints, steps, and exceptions.
- Captures are single-flight: if one capture is already running, the next capture is skipped rather than queued.
- Each capture writes a paired `.png` screenshot and `.json` debugger snapshot to `%USERPROFILE%\Pictures\VSScreenshots`.
- File names use `yyyy-MM-dd__HH-mm-ss-fff_ACTION.ext`, where `ACTION` is `BREAKPOINT`, `STEP-IN`, `STEP-OVER`, or `EXCEPTION`.

## Debugger extraction rules

- Use the Visual Studio `EnvDTE` debugger object model for cross-language Locals, Autos/arguments, values, types, and data members.
- Keep all DTE/debugger reads on the Visual Studio main thread.
- Do not wrap `EnvDTE` expression traversal in `Task.Run`; performance should come from doing less debugger work.
- Serialization and file I/O should happen off the main thread after the snapshot object is built.
- Root-level Locals and Autos are captured first as scalar rows before nested expansion starts.
- Nested extraction is bounded by:
  - max object depth: `2`,
  - max children per object: `100`,
  - max property nodes per root: `1000`,
  - max value length: `10,000` characters,
  - hard extraction cutoff: `1,500 ms`.
- There is no snapshot-wide property-node limit; node budgets are per root variable.
- `ChildrenTotalCount` and `ChildrenSnapshotCount` are used to show when child lists are partial.
- Large values are truncated with an obvious cutoff marker.

## Exception capture rules

- Exception captures still include normal Locals, Autos, and CallStack when available.
- Dedicated exception capture is summary-first: capture the exception message and stack trace by default.
- Do not expand `$exception.DataMembers` by default.
- During exception-triggered captures, keep `$exception` and exception-typed local values scalar-only to avoid expanding expensive runtime exception internals.

## Snapshot UI rules

- Snapshot details use one unified tree view instead of separate accordion/expander sections.
- The detail tree keeps a `Name | Value | Type` table-style layout.
- Top-level detail nodes include Locals, Autos, Watch 1-4, Exception, and CallStack.
- Expanded tree nodes are remembered in-session while the tool window/view model is open.
- The default tool window layout is side-by-side: snapshot list on the left, snapshot detail on the right.
- A layout toggle switches between side-by-side and stacked layouts.
