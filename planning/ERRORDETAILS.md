# ERRORDETAILS: an administrator can investigate every failure

## Goal

Any failure in the suite can be investigated by an administrator of the service it happened in. A failure is any exception that ends a request, a
run, a step, a read or a background job: a driver's failure, a bug in our code, a timeout, a refused file. Its detail is kept with the record of
what failed: the exception's type and message, its inner exceptions, the stack trace and, for a database, the engine's own message and code, all
passed through the redactor. An administrator of the service sees the detail on the console's screens and through the management API. Everyone else,
and every user of a front end (an app, a Flight SQL client, a report tool), sees the classified words and the id of the failure, which they can give
an administrator.

Example: Ana's Sync preflight reads `crm.dbo.orders` with the filter `regoin > 0`. Ana, an approver, sees "The statement was not accepted by the
server. [db 42703] (failure 7f3a...)". Omar, an administrator of the Runner, opens the run on the console and sees PostgreSQL's
`column "regoin" does not exist`, the statement and the stack.

No code loses an exception's details. Code that throws its own error in place of one it caught passes the original as the inner exception,
logs it, or both. That holds in every place an error can be replaced: a `catch` that throws a clearer message, a `Dispose` or `finally` that runs
while an error is already on its way out, and a test's setup and cleanup. Example: a migration fails on SQL Server and the connection closes.
Releasing the migration lock must not throw its own "connection is closed" error in place of the migration's error. The person running the test
sees the migration's error, with the release's problem logged beside it.

The detail can hold values from a row (a duplicate key's value in a constraint message); it is shown to administrators only.

## What exists today

- `ConnectionFailure` (`Shared/src/DbDataKit.Connections.Abstractions`): a driver failure is classified into a kind and a code, and
  `PublicMessage` is fixed text per kind. The engine's own message (`Raw`) is kept only when a provider is made with `includeRawFailures`, and then
  only for local logs; no answer carries it. Only the Gateway's 32-bit ODBC worker turns it on.
- Failures that are not a driver's (an exception in our code, a task host that crashed) reach a run's record or a log as each service wrote them; no
  rule says what an administrator can see.
- The redactor (`IRedactor`, `DbDataKit.Secrets`) scrubs secrets from text.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| ERRORDETAILS-7 | The Shared libraries keep the original exception in every case the check lists for them (153 today), and their entries leave the baseline | none | M | the check passes with no `Shared/` entry in its baseline |
| ERRORDETAILS-8 | The Gateway and Build keep the original exception in every case the check lists for them (22 and 21 today), and their entries leave the baseline | none | S | the check passes with no `DbDataGateway/` or `DbDataBuild/` entry in its baseline |
| ERRORDETAILS-9 | Sync, the Store, the console and the Runner's tests keep the original exception in every case the check lists for them (26 today); the baseline is empty and goes | none | S | the check passes with no baseline file |
| ERRORDETAILS-1 | A shared library for a failure's detail: from any exception it makes the detail (type, message, inner exceptions, stack trace, a driver's own message and code), passed through the redactor, with an id; the answer to a caller carries the classified words and the id; the hosting library keeps the detail of every request that ends in an exception | none | M | a test throws a driver failure and a plain exception in a request; the answer holds the classified words and the id, and the stored detail holds the message, the inner exception and the stack, with a secret in the message redacted |
| ERRORDETAILS-2 | The Runner keeps the detail of each failed run, step and task host, with the run; an administrator of the Runner reads it on the console's run page and through the management API; a viewer, an operator and the console's relayed reads for a person who is not an administrator get the classified words and the id. A preflight finding (the Sync set's, the Build project's) carries the failure's id, so an administrator sees the database's own message beside it | ERRORDETAILS-1 | M | a test fails a read on a misspelled column; an administrator's answer holds the engine's message naming the column, and a viewer's answer holds only `The statement was not accepted by the server. [db 42703]` and the id |
| ERRORDETAILS-3 | The Gateway keeps the detail of each failed request, refresh, Flight SQL query and worker call; an administrator of the Gateway reads it by id on the console; a Gateway's users (apps, Flight SQL clients, report tools) get the classified words and the id | ERRORDETAILS-1 | M | a Flight SQL query on a misspelled column answers the classified words and an id; an administrator reads the engine's message under that id |
| ERRORDETAILS-4 | The console and the Workspace service keep the detail of their own failed requests and jobs, and the console shows an administrator of each service the detail of a failure it relayed | ERRORDETAILS-1 | S | a failed request on each service answers an id; an administrator reads its detail on the console |
| ERRORDETAILS-5 | The flag `includeRawFailures` and the local-only raw text go; the docs say where an administrator finds a failure's detail and who sees what | ERRORDETAILS-2, ERRORDETAILS-3, ERRORDETAILS-4 | S | no provider takes the flag; the docs name the screens and routes |

## Proposed decisions

### ERRORDETAILS-P1: a `catch` with no variable that throws a new exception is a finding (default, awaiting the owner)

`scripts/check_caught_exceptions.py` finds a `throw new` inside a `catch` that names no variable, because such a catch cannot pass the exception it
caught. 58 of the 222 cases in the baseline are of this kind. Example: `catch (FormatException) { throw new FormatException("dpapi reference is not
valid base64."); }` in `DpapiKeyProtector.cs` fails; the fix is `catch (FormatException ex) { throw new FormatException("...", ex); }`.

Options:

- A, as built: a catch with no variable that throws a new exception fails the check.
- B: the check skips such catches, on the reasoning that the author chose not to name the exception; the 58 cases leave the baseline unfixed, and the
  parse error, the missing file or the JSON error behind each one stays lost.

Recommendation: A. The rule in `docs/error-handling.md` is about the original error, and a catch with no variable loses it the same way.

### ERRORDETAILS-P2: the reviewed list of deliberate replacements starts empty (default, awaiting the owner)

The check's `REVIEWED` list lets a catch replace an error without the original, with a reason. It starts empty: every case seen can pass the inner
exception and still throw the error it means to. Candidates that look deliberate:

- `ChromiumExporter.cs`: an `OperationCanceledException` after the deadline becomes `ExportException("deadline", ...)`.
- `OdbcNativeFunctions.cs`: a catalog error after cancellation becomes `OperationCanceledException(cancellationToken)`.
- `ActiveQueries.cs` (Gateway): a cancelled query becomes `RpcException(Cancelled, "The query was cancelled by an administrator.")`.

Each has a constructor that takes the inner exception (`OperationCanceledException(string, Exception, CancellationToken)`, `new Status(code, text,
ex)`), so ERRORDETAILS-7 and ERRORDETAILS-8 can pass it.

Options:

- A, as built: the list starts empty, and these cases are fixed by passing the original.
- B: these cases go on the list with their reason, and their catches stay as they are.

Recommendation: A. A cancellation or a deadline still reads as one, and the original's stack shows where the wait was.

### ERRORDETAILS-P3: the check is a Python script, not a Roslyn analyzer (default, awaiting the owner)

`scripts/check_caught_exceptions.py` reads the C# text: it skips strings, characters, comments and interpolations, then matches each `catch` block
by its braces. It reads the 3,671 tracked files in about 4 seconds, needs no build, and runs in Repository checks with the other Python checks.

Options:

- A, as built: a Python script under `scripts/`, with its tests in `scripts/tests`.
- B: a Roslyn analyzer in every product's build. It would read the syntax tree exactly, and it would report in the editor, but it only runs where a
  product builds, and a change to it rebuilds every product.

Recommendation: A. The script found no false positive among the 222 cases; switch to B only if one turns up that the text cannot tell apart.

## Needs from other subjects

- None.

## Shared areas touched

- `Shared/src/DbDataKit.Connections.*` (failure classification), `DbDataKit.Hosting`, the Runner's run store and API, the Gateway's management API,
  the console's run and server screens.
