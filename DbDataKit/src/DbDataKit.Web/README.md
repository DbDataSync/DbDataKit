# DbDataKit.Web

Trust between a console and the servers it manages (plan 13, W12). So far: the token.

* `ConsoleTokenIssuer` (the console): signs a short-lived (default 5 minutes, at most 10) token with its Ed25519 key. Claims: `iss` (console id), `aud` (the server's instance id), `sub` and `name`, `kind` (`user` or `console`),
  `roles` and per-project `projects`, `jti` (unique), `iat`, `exp`. Format: `base64url(header).base64url(claims).base64url(signature)` with header `{"alg":"EdDSA","typ":"dbdata-console","kid":<key fingerprint>}`.
  It travels in a header, so it works through reverse proxies.
* `ConsoleTokenValidator` (the server): the algorithm must be EdDSA and nothing else; the issuer must be a paired console (`IPairedClients`); the signature must verify with that console's key; then audience, expiry
  (30 s of clock skew forgiven), lifetime, the console's **ceiling** (a token claiming a role the ceiling does not allow is refused, not trimmed), and last that the token id has not been seen (`IReplayCache`).
  A refused token is never remembered, so garbage cannot fill the cache. The result carries a `TokenFailure` for the server's log; a caller is only told it was refused.
* `ServerRole` is a set: Viewer, Operator, Author, Approver, Admin. Operator, Author, Approver and Admin can read, Admin can operate; Admin does not approve plans and Author does not either (`ServerRoles.Close`).
* `ConsoleSigningKey`: the key pair (BouncyCastle Ed25519, since .NET has none), its fingerprint, the 32-byte seed to keep protected.

## Pairing (server side)

A client is a console, another server or a command line (`ClientKind`); the server's list of them is the `pairedClients` list of `server.yaml`, one flow mapping per client (`id`, `name`, `kind`, `publicKey`, `ceiling`, `paired`).

* `PairedClientsFile` reads the list (the file's bytes are compared on every `Find`, so an `unpair` made by the command line takes effect on the server's next call) and edits it surgically (comments and the rest of the file stay). An entry that is not valid is skipped and named in `Problems`, never trusted. Edits take a lock between processes.
* `PairingCodes`: `server pair` makes a code (20 characters, 100 bits, shown once; only its hash is kept in `pairing-codes.json` next to `server.yaml`). It works once, expires (10 minutes by default), and five wrong codes in a row cancel every code waiting.
* `PairingService.Pair(request)`: checks the shape of the request, then spends the code and adds the client with the ceiling the code carried. Pairing an id again replaces its entry (a new key). The outcome carries the server's instance id and certificate fingerprint for the client to pin. The HTTPS endpoint that calls it is `PairingEndpoint.MapPairing` (`GET` says which server and API version; `POST` pairs; a refusal is a `403` that does not say why).
* `PairingVerbs.Create(files)`: `pair [--max-role <roles>] [--name <label>] [--minutes <n>]`, `clients`, `unpair <client>` for a product's `server` noun (`Noun.With`). `--max-role` defaults to admin.

## Pairing (client side)

* **The code never crosses the wire.** Both ends hold a key made from the code (the hash the server keeps). The client's proof (`PairingProof.ForRequest`) covers its id, public key, kind and the fingerprint of the certificate it saw; the server's (`ForResponse`) covers its instance id, its certificate and the client. Someone who terminates TLS with their own certificate cannot make either, so the client pins the server it meant to and the server pairs the client that holds the code.
* `PairingClient.PairAsync(url, code, identity, kind, name)`: looks at the certificate, proves, checks the server's proof, and returns a `PairedServer` (instance id, certificate fingerprint to pin, API version, ceiling). `PairingClient.Pinned(fingerprint)` is the handler for every later call: it accepts that certificate and no other, whatever it chains to. A server's renewed certificate needs pairing again.
* `ConsoleKeyStore` keeps the console's id and signing seed in the secret store (`dbdata:kit:console:signing-key`); a damaged entry is reported, never replaced. `InstanceRegistry` is the console's `instances.yaml` (id, name, url, certificate fingerprint, API version); an entry without a pin, or over plain HTTP, is not trusted.

## Management API version

`ManagementApi.Version` is the integer version of the management API (plan 13, D17). A server says it at pairing and in the `X-DbData-Api-Version` header of every response (`UseManagementApiVersion()`); a client supports its own version and the one before (`ManagementApi.Supports`), and `ManagementVersionHandler` (what `PairingClient.Pinned` returns) states the client's version on every request and refuses the response of a server outside that range, or one that reports none, with `IncompatibleServerException` and a message that says which side to upgrade. Pairing checks it before it sends anything.

## Key rotation

A client replaces its signing key without pairing again. `KeyRotationRequest` carries the new public key signed by the old key (it is the paired client asking) and by the new one (it holds what it registers), over the server's instance id, the client's id, a time and a nonce; `KeyRotationService` (the server, `MapKeyRotation`) checks both signatures, the minute either side of now and that the nonce is new, and replaces the key and nothing else. Old tokens fail on the next call, new ones pass. Asking again for the key already on record, signed by it, succeeds and changes nothing, so a console that did not hear the answer can retry.

`ConsoleKeyRotation.RunAsync(keys, registry)` is the console's side: `ConsoleKeyStore.BeginRotationAsync` makes the new key and keeps it beside the current one, each server is asked to take it, `InstanceRegistry.SetKey` records which key each server has (so the console signs for each server with the key that server knows, `ForKeyAsync`), and only when every server has the new key does `PromoteNextAsync` make it the current one. A server that could not be reached stays on the old key, which keeps working for it; running the rotation again continues.

The role check per command and the audit are in `ManagementAuthorization` and `ManagementIdentity`; the key rotation, the version rule and the server-to-server kind are the next slices (T07, T09, T10).

## Publishers and owner scope (`OwnerScope`)

A server client that publishes into a server (a Build server creating Proxy data sources) holds `ServerRole.Publisher`, whose ceiling is set at pairing (`server pair --max-role publisher`). `OwnerScope.Authorize(token, action, object)` is the rule the called server applies on every call: a Publisher may create (the object is then stamped `managed_by: <client id>`, `OwnerScope.Stamp`) and may change or delete only objects carrying its own id; an object made by hand, one that belongs to another client and the server's own policy (`ManagedObject.IsServerPolicy`) are refused with a reason for the log. An Admin is not limited by owner. Taking an object over (`--adopt`) is `OwnerScope.AuthorizeAdopt`: a person's act with the admin role, never a server client's.

## Authenticating calls (`ManagementAuthentication`)

`UseManagementAuthentication(validator, refused)` puts the token check in front of every request under `/api/`: the token is read from the `X-DbData-Token` header and validated by `ConsoleTokenValidator`, and the validated token is on the request for the endpoint (`context.ManagementToken()`), which decides what the caller may do with `ManagementAuthorization`. A refused call is a `401` with no body; `refused` hears why, for the server's log. Pairing and key rotation are outside the prefix because a client that is not paired yet has no token. The first server to use it is the Build server (R01).

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
