# Debug Capture Performance Learnings And Recommendations

- Performance data source
  - Output Window metrics captured from `2026-09-20 00:42:58` to `2026-09-20 00:46:07`.
  - The analyzed data includes breakpoint captures, exception captures, screenshot capture, aggregate extraction counters, root-level timings, accounted/unaccounted extraction time, and file size metrics.
  - The metrics are cumulative checkpoint timings, so exclusive phase timings are inferred from checkpoint deltas.
  - Some output lines are asynchronous and can appear slightly out of order.

- High-level learnings
  - The main bottleneck is debugger snapshot extraction through EnvDTE.
	- Locals extraction is still the dominant part of snapshot extraction.
  - The newest root-level metrics identify the real hot path much more clearly: `DataMembers` enumeration and expansion of the `person` and `persons` roots dominate extraction time.
  - Accounted extraction time is now almost complete at about `99.9%` to `100%`, so the earlier suspected missing time is now explained by collection/member enumeration.
  - `Name`, `Value`, `Type`, scalar root reads, and JSON serialization are not meaningful bottlenecks.
  - Screenshot capture is not the primary bottleneck because it normally completes while snapshot extraction is still running.
  - JSON serialization and JSON file writing are very small compared with debugger extraction.
	- The first and one later capture still had large debugger UI idle waits, but normal captures are mostly dominated by extraction rather than idle wait.

- Breakpoint capture learnings
	- Average breakpoint capture was about `1,867 ms` across `18` breakpoint captures.
  - Average breakpoint capture was about `1,692 ms` when excluding the two largest debugger-idle outliers.
  - Fastest observed breakpoint capture was about `773 ms`.
  - Slowest observed breakpoint capture was about `3,324 ms`.
  - The largest debugger UI idle waits were about `2,422 ms` and `2,073 ms`.
  - Normal debugger UI idle waits were often around `150 ms` to `545 ms`.

- Exception capture learnings
	- Exception captures took about `1,979 ms` to `2,763 ms`.
  - Average exception capture was about `2,368 ms`.
  - Exception summary extraction was relatively small at about `61 ms` to `115 ms`.
  - Exception capture is still dominated by Locals extraction, not by message or stack trace extraction.
	- All `3` exception captures hit the extraction time cutoff.
  - Keeping `$exception` and exception-typed values scalar-only appears directionally correct, because exception-specific summary work is not the bottleneck.

- Debugger snapshot extraction learnings
	- Breakpoint snapshot export averaged about `1,286 ms`.
  - Breakpoint snapshot export ranged from about `585 ms` to `1,805 ms`.
  - Exception snapshot export ranged from about `1,586 ms` to `1,697 ms`.
  - Locals checkpoint timings ranged from about `571 ms` to `1,783 ms`.
  - Autos usually added only about `7 ms` to `18 ms` after Locals.
  - CallStack usually added only about `1 ms` to `5 ms` after Autos.
	- The performance problem is therefore concentrated in nested local expansion, especially `DataMembers` enumeration.
  - `9` of `18` breakpoint captures hit the extraction cutoff.
  - `3` of `3` exception captures hit the extraction cutoff.
  - Captured breakpoint property nodes ranged from `125` to `179`.
  - Breakpoint captures consistently had `10` Locals roots and `1` Autos root.
  - Exception captures had `11` Locals roots and `1` Autos root.
  - Per-root node budget was never hit, so `MaxNodesPerRoot=1000` is not the current limiting factor.
	- Time budget, depth limit, and `DataMembers` traversal cost are the active limiting factors.
  - Max-depth hits were high, ranging from `91` to `144` in breakpoint captures.
  - Aggregate `DataMembers` access timing was modest, but `DataMembers` enumeration timing was very high.
  - `DataMembers` enumeration ranged from about `515 ms` to `1,690 ms` in breakpoint captures.
  - `DataMembers` enumeration often accounted for almost the entire extraction time.
  - `Expressions` access/count/enumeration was usually small compared with `DataMembers` enumeration, although one sample showed `ExpressionsAccessMs=231 ms`.
  - Accounted extraction time was about `99.9%` to `100%`, so the instrumentation now explains the bottleneck well.

- Screenshot learnings
	- Screenshot capture/write was stable at about `252 ms` to `546 ms`.
  - Screenshot capture averaged about `297 ms` for breakpoint captures.
  - Pixel capture and PNG encoding make up almost all screenshot time.
  - PNG file writing itself appears small.
	- Breakpoint PNG sizes ranged from about `232 KB` to `305 KB`.
  - Exception PNG sizes ranged from about `216 KB` to `292 KB`.
  - Screenshot optimization is lower priority than debugger extraction optimization.

- JSON loading and UI learnings
	- The newest data sample did not include snapshot browser refresh timings.
  - Prior snapshot browser data still suggests the selected snapshot detail UI is not currently a bottleneck.
  - Breakpoint JSON sizes ranged from about `18 KB` to `26 KB`.
  - Exception JSON sizes ranged from about `19 KB` to `23 KB`.

- Instrumentation recommendations
  - The previous requested aggregate timers and counts are now working and produced useful data.
	- The root timing and accounted-time metrics are now strong enough to identify `person`, `persons`, and `DataMembers` enumeration as the main hot path.
  - Add per-root child count and captured child count to the slow-root summary.
  - Add a reason field to slow-root entries, such as `time-budget-hit`, `max-depth-hit`, or `child-limit-hit`.
  - Add separate counters for child-limit truncation versus time-budget truncation.
  - Add a configuration switch to temporarily disable expansion for named slow roots so before/after performance can be compared.
  - Split snapshot browser refresh metrics into file enumeration, file read, JSON deserialize, sorting, collection update, selected item sync, detail tree build, and WPF layout/render delay.

- Optimization recommendations
  - Prioritize reducing Locals extraction cost before optimizing screenshots or JSON writing.
	- Prioritize reducing or deferring expansion of the `person` and `persons` roots.
  - Consider scalar-only automatic capture for complex object roots and defer child expansion until the user asks for it.
  - Consider lower default nested extraction limits for automatic breakpoint captures.
  - Consider lowering the default automatic capture depth or using a smaller automatic time budget when the user is stepping quickly.
  - Consider a per-root time budget so one slow root cannot consume almost the entire snapshot budget.
  - Consider a per-root child enumeration cap below the current `100` when a root is known to be expensive.
  - Consider using different extraction limits for exception captures, because exceptions are often more latency-sensitive.
  - Keep exception summary extraction lightweight and avoid expanding exception internals by default.
  - Add a bounded timeout for debugger UI idle wait so a capture cannot spend several seconds waiting before doing useful work.
  - Consider skipping screenshot capture when the debugger snapshot extraction has already exceeded the target latency budget.
  - Consider appending only the newly captured JSON snapshot to the tool window instead of reloading the whole snapshot folder after every capture.
  - Keep full folder reload for manual refresh.
  - Consider virtualizing or batching snapshot list updates if the snapshot folder grows significantly.

- Code reference table

| Linenumber | Filename | Description | Code First Line |
| --- | --- | --- | --- |
| 46 | [Services/DebuggerVariableExportService.cs](./Services/DebuggerVariableExportService.cs#L46) | Snapshot export timer starts here. | `using var timer = PerformanceTimer.Start` |
| 70 | [Services/DebuggerVariableExportService.cs](./Services/DebuggerVariableExportService.cs#L70) | Build snapshot timer starts here. | `using var timer = PerformanceTimer.Start` |
| 104 | [Services/DebuggerVariableExportService.cs](./Services/DebuggerVariableExportService.cs#L104) | Locals extraction is timed here. | `snapshot.Locals = BuildExpressionList` |
| 204 | [Services/DebuggerVariableExportService.cs](./Services/DebuggerVariableExportService.cs#L204) | Expression list extraction begins here. | `private static List<SnapshotProperty>` |
| 255 | [Services/DebuggerVariableExportService.cs](./Services/DebuggerVariableExportService.cs#L255) | DataMembers extraction begins here. | `private static List<SnapshotProperty>` |
| 309 | [Services/DebuggerVariableExportService.cs](./Services/DebuggerVariableExportService.cs#L309) | Snapshot property values are read here. | `private static SnapshotProperty?` |
| 34 | [Services/ScreenshotCaptureService.cs](./Services/ScreenshotCaptureService.cs#L34) | Screenshot timer starts here. | `using var timer = PerformanceTimer.Start` |
| 45 | [Services/ScreenshotCaptureService.cs](./Services/ScreenshotCaptureService.cs#L45) | PNG capture and encoding happens here. | `var pngBytes = CapturePngBytes` |
| 32 | [Services/SnapshotRepository.cs](./Services/SnapshotRepository.cs#L32) | JSON snapshot load timer starts here. | `using var timer = this.logger` |
