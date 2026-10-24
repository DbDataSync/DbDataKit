# DbDataKit.Web

Trust between a console and the servers it manages (plan 13, W12). So far: the token.

* `ConsoleTokenIssuer` (the console): signs a short-lived (default 5 minutes, at most 10) token with its Ed25519 key. Claims: `iss` (console id), `aud` (the server's instance id), `sub` and `name`, `kind` (`user` or `console`),
  `roles` and per-project `projects`, `jti` (unique), `iat`, `exp`. Format: `base64url(header).base64url(claims).base64url(signature)` with header `{"alg":"EdDSA","typ":"dbdata-console","kid":<key fingerprint>}`.
  It travels in a header, so it works through reverse proxies.
* `ConsoleTokenValidator` (the server): the algorithm must be EdDSA and nothing else; the issuer must be a paired console (`IPairedConsoles`); the signature must verify with that console's key; then audience, expiry
  (30 s of clock skew forgiven), lifetime, the console's **ceiling** (a token claiming a role the ceiling does not allow is refused, not trimmed), and last that the token id has not been seen (`IReplayCache`).
  A refused token is never remembered, so garbage cannot fill the cache. The result carries a `TokenFailure` for the server's log; a caller is only told it was refused.
* `ServerRole` is a set: Viewer, Operator, Author, Approver, Admin. Operator, Author, Approver and Admin can read, Admin can operate; Admin does not approve plans and Author does not either (`ServerRoles.Close`).
* `ConsoleSigningKey`: the key pair (BouncyCastle Ed25519, since .NET has none), its fingerprint, the 32-byte seed to keep protected.

The pairing, the local channel, the role check per command and the audit are the next slices (T02 to T09).

## Audit and authorship

`ManagementIdentity.Entry(token, command, target, outcome, at)` makes the `AuditEntry` for a change (user and console from the token, never a value) and `IAuditLog` records it; `FileAuditLog` keeps JSON
lines. `ManagementIdentity.CommitAuthor(token)` gives the name and address a configuration commit is authored by, so the history of a config repository says who changed what and through which
console; `LocalAuthor` does the same for the local channel.

## Write version checks

`WriteGuard` makes a management write name the version it was based on. `RunAsync(key, expectedVersion, currentVersion, write)` takes a per-key lock, reads the current version
under it, and runs the write only if the two match (a null expected version means "create": nothing may be there). A stale write returns `WriteResult.Stale(currentVersion)` (or
throws `StaleWriteException` from `RunOrThrowAsync`), so the caller can read again, merge and retry. What a "version" is stays the caller's choice (a content hash for a file); it is
read from the thing itself every time, so a change made outside the guard is caught too.


## Session sign-in (`DbDataKit.Web.Auth`)

How the people who use a product's own web console sign in (plan 13, U05), moved out of `DbDataSync.Api`: `SessionAuthenticationHandler` (the session cookie, re-read from the state database on every request, with the
loopback / remote network trust as the fallback), `PasskeyService` and `PasskeyOptions` (WebAuthn through fido2-net-lib, the relying-party checks), `WindowsSignIn` (a negotiated Windows identity and the admin / viewer
groups), `BootstrapInvite` (the first-run invitation), `CurrentUser` (who is acting, and the author of a config commit) and `Policies`. The users, sessions and invites are `DbDataKit.State`'s.

Nothing in it is one product's. `AuthProduct("DbDataSync")` derives the configuration section (`DbDataSync:Auth`), the cookie (`dbdatasync.session`), the scheme, the user-id claim, the invite command and the author a
product's own changes are attributed to, so two products on one machine never share a cookie. A product passes its `AuthProduct` to `AuthOptions.FromConfiguration` and `PasskeyOptions.FromConfiguration` and registers
the handler under `AuthProduct.SchemeName`. Sync's `SyncAuth` (in `DbDataSync.Api.Auth`) does that and adds static extensions so its call sites read as they always did.
