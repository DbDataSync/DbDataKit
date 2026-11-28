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
| ERRORDETAILS-8 | The Gateway and Build keep the original exception in every case the check lists for them (22 and 21 today), and their entries leave the baseline | none | S | the check passes with no `DbDataGateway/` or `DbDataBuild/` entry in its baseline |
| ERRORDETAILS-9 | Sync, the Store, the console and the Runner's tests keep the original exception in every case the check lists for them (26 today); the baseline is empty and goes | none | S | the check passes with no baseline file |
| ERRORDETAILS-10 | Each process has one redactor (the existing `DbDataKit.Secrets.Redactor` / `IRedactor`); every secret value the process resolves is registered with it; the hosting library's logging passes every message and exception text (`ex.ToString()`, inner exceptions included) through it; the separate copies (`Connections.Http/SecretRedaction`, the State `Redactor` in `StateEndpoints.cs`, and any other redaction that keeps its own list of values) use it instead | none | M | a test resolves a secret, throws an exception whose message and inner exception hold it, logs it through the hosting library, and the log line shows `***` |
| ERRORDETAILS-1 | A shared library for a failure's detail: from any exception it makes the detail (type, message, inner exceptions, stack trace, a driver's own message and code), passed through the process's redactor of ERRORDETAILS-10, with an id; the answer to a caller carries the classified words and the id; the hosting library keeps the detail of every request that ends in an exception | ERRORDETAILS-10 | M | a test throws a driver failure and a plain exception in a request; the answer holds the classified words and the id, and the stored detail holds the message, the inner exception and the stack, with a secret in the message redacted |
| ERRORDETAILS-2 | The Runner keeps the detail of each failed run, step and task host, with the run; an administrator of the Runner reads it on the console's run page and through the management API; a viewer, an operator and the console's relayed reads for a person who is not an administrator get the classified words and the id. A preflight finding (the Sync set's, the Build project's) carries the failure's id, so an administrator sees the database's own message beside it | ERRORDETAILS-1 | M | a test fails a read on a misspelled column; an administrator's answer holds the engine's message naming the column, and a viewer's answer holds only `The statement was not accepted by the server. [db 42703]` and the id |
| ERRORDETAILS-3 | The Gateway keeps the detail of each failed request, refresh, Flight SQL query and worker call; an administrator of the Gateway reads it by id on the console; a Gateway's users (apps, Flight SQL clients, report tools) get the classified words and the id | ERRORDETAILS-1 | M | a Flight SQL query on a misspelled column answers the classified words and an id; an administrator reads the engine's message under that id |
| ERRORDETAILS-4 | The console and the Workspace service keep the detail of their own failed requests and jobs, and the console shows an administrator of each service the detail of a failure it relayed | ERRORDETAILS-1 | S | a failed request on each service answers an id; an administrator reads its detail on the console |
| ERRORDETAILS-5 | The flag `includeRawFailures` and the local-only raw text go; the docs say where an administrator finds a failure's detail and who sees what | ERRORDETAILS-2, ERRORDETAILS-3, ERRORDETAILS-4 | S | no provider takes the flag; the docs name the screens and routes |

The fixes of ERRORDETAILS-8 and ERRORDETAILS-9 pass the original in the cancellation and deadline cases too (the Gateway's `ActiveQueries.cs`). A case the script misreads while fixing them goes to the owner: it is the reason to
reconsider a Roslyn analyzer in place of the script.

## Proposed decisions

### ERRORDETAILS-P4: a classified driver failure drops the original, and the check does not see it (awaiting the owner)

Eight throws in the Shared libraries turn a caught exception into a `ConnectionFailureException` through a classifier and pass no inner exception:
`FailureClassifier.ToException` (used by `Connections/DbProviderBase.cs` and `Semantic.Connections/ConnectionsSourceExecutor.cs`) and
`new ConnectionFailureException(...Classify(e, ...))` in `Connections.Files` (`FileStores.cs`, `Ftps.cs`, `Smb.cs`, `SshStores.cs` twice) and
`Connections.Http/HttpFailures.cs`. `scripts/check_caught_exceptions.py` passes them, because the caught variable appears in the throw. Two tests
assert the inner exception is null: `Open_failures_become_classified_failures_without_driver_text` (`Shared/tests/DbDataKit.Connections.Tests/ProviderTests.cs`)
and `Public_text_never_contains_raw_driver_text` (`Shared/tests/DbDataKit.Connections.Abstractions.Tests/FailureTests.cs`).

Example: Omar's SQL Server refuses the login of a connection. The provider throws "The server refused the sign-in. [db 28000]" with no inner
exception, so the log and, later, the stored detail have neither SqlClient's message nor its stack.

Options:

1. Pass the original as the inner exception in all eight (`ToException` and `ConnectionFailureException` take it), change the two tests to assert
   that the message stays classified and the inner exception is the driver's, and teach the check that a variable only passed to another call does
   not count.
2. Leave them as they are until ERRORDETAILS-1 keeps the detail.

Recommendation: 1, as the answer about the seven catches whose message can hold a secret: the original is kept and the redaction happens where the
detail is written (ERRORDETAILS-10). It is also the case of a misread script that the goal says goes to the owner: the reason to reconsider a
Roslyn analyzer.

## Needs from other subjects

- None.

## Shared areas touched

- `Shared/src/DbDataKit.Connections.*` (failure classification), `DbDataKit.Hosting`, the Runner's run store and API, the Gateway's management API,
  the console's run and server screens.
