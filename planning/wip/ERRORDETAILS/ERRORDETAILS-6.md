# ERRORDETAILS-6 (work in progress)

## Built so far

- `scripts/check_caught_exceptions.py`: reads every tracked `.cs` file (strings, characters and comments blanked), finds each `throw new` inside a
  `catch` (the innermost one that holds it) and fails, naming file and line, when the `new`'s arguments do not pass the caught variable. Passing it
  anywhere in the argument list counts (`innerException: ex`, `new Status(code, msg, ex)`, `ex.InnerException`); `ex.Message` or `$"{ex}"` does not.
  `REVIEWED` (empty) holds deliberate replacements as (file, piece of the throw's text) -> reason; a stale entry fails.
- `scripts/caught_exceptions_baseline.txt`: the 121 files and their counts (222 throws) found today; it only shrinks (more fails and lists each throw,
  fewer fails until the line is lowered or deleted).
- `scripts/tests/test_check_caught_exceptions.py`: 15 tests, including the "Done when" pair (`catch (Exception ex) { throw new InvalidOperationException("x"); }`
  fails, passing `ex` passes).
- A step in `.github/workflows/repo-checks.yml` (job of the Python checks), and a paragraph in `docs/error-handling.md` naming the check.

## Not built yet

- The fixes of the 222 findings: stopped before mass-fixing, as the session asked (more than about 40).
- `Dispose`/`finally` cases: none fixed yet (they come with the fixes).
- The second "Done when" (a migration failing inside the lock reports the migration's error): `MigrationLockReleaseTests` is on
  `backup/state-lock-1`, not yet on `main`; point at it once it lands.

## Findings by product (222 throws in 121 files)

- Shared/src 153; DbDataGateway/src 22; DbDataBuild 21 (15 src, 6 tests); DbDataSync/src 14; DbDataStore/src 7; DbDataKit/src 3; DbDataRunner/tests 2.
- 58 of them are in a catch with no variable (`catch (JsonException) { throw new InvalidDataException("... is not a list ..."); }`).
- Many need a new constructor on the thrown type (`StoreConnectionException(string)` has no `(string, Exception)`).

## Open questions

- Q1: fix all 222 now, or keep the baseline and fix by product? Example: `DbDataStore/src/DbDataStore.Server/StoreConnections.cs:57`,
  `catch (SecretNotFoundException ex) { throw new StoreConnectionException($"... {ex.Message}"); }`; fixing it adds a `(string, Exception)` constructor
  to `StoreConnectionException` and passes `ex`. Options: (A) all in this branch, about 120 files; (B) the check lands with the baseline now, so no new
  case gets in, and the fixes follow as tasks by product (Shared first); (C) all now, but one branch per product. Recommendation: B.
- Q2: `CLAUDE.md` and `.claude/agents/worker.md` hold the list of checks to run before a push; both change only with the owner's approval. Add
  `&& python3 -I scripts/check_caught_exceptions.py` to both lists?

## Defaults taken

- D1: the baseline file, which only shrinks, so the check can run in CI today. Example: a new `catch (IOException ex) { throw new X(ex.Message); }`
  in a file not on the list fails the pull request; the 2 throws already in `UserStore.cs` pass until someone fixes them and lowers the count.
- D2: a catch with no variable that throws a new exception is a finding. Example: `catch (FormatException) { throw new FormatException("not base64"); }`
  fails; the fix is `catch (FormatException ex) { throw new FormatException("not base64", ex); }`.
- D3: deliberate replacements are rare: `REVIEWED` is empty. Candidates seen (all can pass the inner exception instead): the export deadline in
  `ChromiumExporter.cs` (`OperationCanceledException` -> `ExportException("deadline", ...)`), `OdbcNativeFunctions.cs` turning a catalog error after
  cancellation into `OperationCanceledException(ct)`, the Gateway's "cancelled by an administrator" `RpcException`.
- D4: a Python check, not a Roslyn analyzer: it reads strings, comments and interpolations well enough for the 3,671 files in 4 s, and needs no build.
  An analyzer would be exact but runs only where a product builds.
