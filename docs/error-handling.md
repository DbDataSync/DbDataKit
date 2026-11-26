# Error handling
<!-- reviewed: 2026-11-26 -->

An error is investigated once. Whoever reads it, an administrator, a developer or a CI log, gets the original exception with everything it carried,
so the cause is understood and fixed in one pass.

**Replacing an error keeps the original.** A `catch` that throws a clearer error passes the caught exception as the inner exception:

```csharp
catch (SqlException ex)
{
    throw new StateMigrationException($"Migration {version} of '{module}' failed.", ex);
}
```

Throwing `new StateMigrationException("Migration failed.")` without `ex` loses the engine's message, its code and the stack.

**Cleanup never hides the first error.** A `Dispose`, `finally` or test cleanup that runs while an exception is already on its way out must not throw
in its place. It checks whether it has anything to do (a lock on a connection that is closed already ended with the session). When its own step fails,
it logs that failure and lets the first exception through.

Example: a migration fails on SQL Server and the server ends the session. Releasing the migration lock finds the connection closed and does nothing,
and the test reports the migration's error.

**Catching without rethrowing logs the exception**, the whole exception, not only its message, unless the case is expected and named in a comment
beside the `catch` (a file that may not exist, a retry that is about to happen).

**What a person is shown** is a separate rule: a user of a front end gets classified words and an id; an administrator gets the full detail
(`docs/security.md`). Both come from the same kept exception, so nothing here depends on who reads it.
