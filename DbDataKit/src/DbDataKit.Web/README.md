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
