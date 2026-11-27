# ERRORDETAILS-7 (work in progress)

Built so far: 146 of the 153 `Shared/` cases of `scripts/caught_exceptions_baseline.txt` pass the caught exception as the inner exception. Each
exception type that had no constructor taking one got an optional `Exception? inner = null` passed to its base. Catches with no variable name it.
The cancellation cases pass the original (`new OperationCanceledException("The operation was canceled.", ex, token)`, the text .NET gives one made
from a token alone). No message people see was changed.

Not built: 7 cases, left in the baseline, because the code drops the original on purpose so that a secret does not travel with it (question 1).

## Question 1: a caught exception whose message can hold a secret

The code says why each one drops the original:

- `Connections.Files/SshStores.cs`: a private key that does not read. SSH.NET's message "can hold part of the key".
- `Connections.Http/HttpConnections.cs`: a client certificate that does not load. The loader's message "can hold a path or part of the file".
- `Connections/ComposerBase.cs`: a connection string the driver's builder refuses. The builder's message "can quote the offending text", passwords
  included.
- `State.Engines/Snapshots/StateEndpoints.cs` (2), `StateMoveService.cs` (1), `StateSnapshotService.cs` (1): a state move or snapshot import.
  The message is passed through the state redactor because it can hold the resolved connection string. `StateSnapshotService` throws only when the
  redactor changed the message, which means the message holds a secret.

Example: Ana moves the state to PostgreSQL with `Password=secret:state-db`, and the driver's error quotes the resolved connection string. Today the
answer and the log show the redacted text. If the original became the inner exception, a log that writes `ex.ToString()` would print the password.

Options:

1. Pass the original anyway, and rely on the detail store of ERRORDETAILS-1 running the redactor. Every log that writes `ex.ToString()` before that
   exists prints the secret.
2. Put the 7 cases on `REVIEWED` in `scripts/check_caught_exceptions.py`, each with its reason.
3. Pass a stand-in as the inner exception: a shared `RedactedException` that keeps the original's type name and stack trace (and its inner chain,
   handled the same way) and either the redacted message (State, where the redactor knows the secrets) or no message (SSH key, certificate,
   connection string, where nothing knows which part is secret).

Recommendation: 3. The administrator gets the type and the stack, which say where it failed, and no secret is written anywhere. It is a small
shared type, and ERRORDETAILS-1 can use it for the detail store too.

## Defaults taken

None.
