# DbDataKit.Identity

The identity home (plan 13a U13): who people are and what they may do, for every server, console and app of an install. A component, not a host: the serving layer hosts it (plan 20, O06) and it can run on its own (U15).

- **`HomeSchema`**: the tables, over `DbDataKit.State`'s users, credentials and sessions: groups, members, role assignments, OpenID Connect clients, authorization codes, signing keys. SQLite, SQL Server and PostgreSQL by the state dialect.
- **`HomeDirectory`**: groups, and roles assigned to a user or a group on a server, on one Build project of a server, or in an app. What a user holds is the union of their own and their groups'.
- **`OpenIdProvider`** and **`MapOpenIdProvider`**: discovery, JWKS, `authorize`, `token`, `userinfo`. Authorization code with PKCE (S256 only), ES256 id tokens and access tokens. `sub` is the home's user id; `groups` and `dbdata_roles` (`server:<id>`, `server:<id>/<project>`, `app:<id>`) carry what the home says. The host signs the person in (its session) and tells the endpoints who that is.
- **`SigningKeys`**: ES256 keys, the private half protected by the host's `ISigningKeyProtector` (there is no default, so there is no unprotected key); rotate, retire.
- **`HomeTokens`**: the request for the console's token to a server (plan 13 T01) with the home's user id as the subject and the roles the home holds for that user there.

Not here yet: federation of Windows (Negotiate) and Entra groups into roles (a row of its own), refresh tokens, the console as a client (U14), running the home on its own (U15), the module in the serving layer (O06).
