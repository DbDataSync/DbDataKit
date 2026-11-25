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
| ERRORDETAILS-1 | A shared library for a failure's detail: from any exception it makes the detail (type, message, inner exceptions, stack trace, a driver's own message and code), passed through the redactor, with an id; the answer to a caller carries the classified words and the id; the hosting library keeps the detail of every request that ends in an exception | none | M | a test throws a driver failure and a plain exception in a request; the answer holds the classified words and the id, and the stored detail holds the message, the inner exception and the stack, with a secret in the message redacted |
| ERRORDETAILS-2 | The Runner keeps the detail of each failed run, step and task host, with the run; an administrator of the Runner reads it on the console's run page and through the management API; a viewer, an operator and the console's relayed reads for a person who is not an administrator get the classified words and the id. A preflight finding (the Sync set's, the Build project's) carries the failure's id, so an administrator sees the database's own message beside it | ERRORDETAILS-1 | M | a test fails a read on a misspelled column; an administrator's answer holds the engine's message naming the column, and a viewer's answer holds only `The statement was not accepted by the server. [db 42703]` and the id |
| ERRORDETAILS-3 | The Gateway keeps the detail of each failed request, refresh, Flight SQL query and worker call; an administrator of the Gateway reads it by id on the console; a Gateway's users (apps, Flight SQL clients, report tools) get the classified words and the id | ERRORDETAILS-1 | M | a Flight SQL query on a misspelled column answers the classified words and an id; an administrator reads the engine's message under that id |
| ERRORDETAILS-4 | The console and the Workspace service keep the detail of their own failed requests and jobs, and the console shows an administrator of each service the detail of a failure it relayed | ERRORDETAILS-1 | S | a failed request on each service answers an id; an administrator reads its detail on the console |
| ERRORDETAILS-5 | The flag `includeRawFailures` and the local-only raw text go; the docs say where an administrator finds a failure's detail and who sees what | ERRORDETAILS-2, ERRORDETAILS-3, ERRORDETAILS-4 | S | no provider takes the flag; the docs name the screens and routes |

## Needs from other subjects

- None.

## Shared areas touched

- `Shared/src/DbDataKit.Connections.*` (failure classification), `DbDataKit.Hosting`, the Runner's run store and API, the Gateway's management API,
  the console's run and server screens.
