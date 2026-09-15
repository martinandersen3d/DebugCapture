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