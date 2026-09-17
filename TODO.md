# TODO

Open items to think about, found while reviewing sample snapshot output
(`2026-09-17__23-27-30-382_BREAKPOINT.json` / `2026-09-17__23-27-35-419_EXCEPTION.json`).

2. **Exception data duplicated between `Locals` and `Exception`** — In the `EXCEPTION.json`
   sample, the full `$exception` object appears both inside `Locals` (as a debugger local) and
   again in the dedicated `Exception` field, with heavy overlap (Message, StackTrace, HResult,
   etc. repeated). This roughly doubles the exception payload size. Decide whether to exclude
   `$exception` from `Locals`/`Autos` since it's redundant with the dedicated `Exception` field.

3. **Very deep/wide expansion for `$exception` local, inconsistent truncation** — The
   `Locals[$exception].Children` walks deep into runtime internals (e.g.
   `TargetSite.DeclaringType.*`, large byte arrays like `SerializationWatsonBuckets`, internal
   `ListDictionaryInternal` structures), and does not report `MembersTruncated` even though it's
   clearly runaway, unlike the dedicated `SnapshotException.Members` which does. Consider
   applying the same depth/count truncation logic uniformly to all locals (not just the
   dedicated exception builder) to avoid oversized, hard-to-read snapshot files.
