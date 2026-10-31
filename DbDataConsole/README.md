# dbdatakit, the console

The one web interface for the DbData servers (plan 13, U07). It signs people in, holds the users, and (in the slices that follow) manages the Sync, Proxy and Build servers paired with it.
It is a separate host from the servers: a server runs without it, and it is not needed to run one.

```
dbdatakit server run [--repo <console directory>] [--listen https://localhost:8480]
dbdatakit user list [--repo <console directory>]
dbdatakit user invite [--role admin|viewer] (--email <address> | --user <id>) [--url <address>]
dbdatakit user set-email --user <id> --email <address>
dbdatakit instance add <https address> --code <code> [--name <label>] [--repo <console directory>]
dbdatakit instance list [--offline] [--repo <console directory>]
dbdatakit instance remove <id or name> [--repo <console directory>]
dbdatakit instance rotate-key [--repo <console directory>]
dbdatakit service install|uninstall|status|start|stop|restart|doctor|unit
dbdatakit health check [--url <url>]
```

The console's directory is `--repo`, or `DBDATAKIT_CONSOLE_DIR`, or a folder under the user's local application data (`%ProgramData%\DbDataKit\console` for the Windows service). It holds `state.db`
(users, sessions, invitations, notifications), the self-signed certificate (`tls.pfx` and its password file, readable only by the account that runs the console) and `FIRST-RUN.txt`.

## First run

With no user yet, the console writes an invitation to `FIRST-RUN.txt` and the log. Open `https://localhost:8480/invite#<code>` once and register a passkey: that person is the first administrator.
**The state database** is SQLite (`state.db` in that directory) unless you name another engine: `--state-engine mssql|postgres --state-connection "<connection string>"` on `server run`, or `DBDATAKIT_CONSOLE_STATE_ENGINE` and `DBDATAKIT_CONSOLE_STATE_CONNECTION` (the commands that open it, such as `user invite`, read the variables too). The password belongs in a reference, not in the string: `Password=env:STATE_DB_PASSWORD` (the console has no secret command yet, so its own store holds only its keys). The engine is chosen once; changing it starts an empty store. The console needs a database of its own: the Proxy's state database has tables of the same names.

`dbdatakit user invite` prints a fresh invitation when that one has expired or scrolled away (it opens the state database directly, so it works when nobody can sign in).

## One account per person

A person is one user however they sign in: a local passkey, a Windows (Negotiate) account, or the identity home (plan 13a U16). The **email address is the account's key** (for a Windows account, its user principal name, `user@domain`): it is
trimmed and compared without regard to case, and one address is one account. The first sign-in by a way no account has seen joins the account that holds the person's address, so they have the same id (the audit's) and the same roles;
a person the home knows takes over the local account with the home's id, and everything the account had (its passkeys, Windows accounts, sessions) moves with it. A sign-in that would take an address another account holds is refused with a message.

What is trusted to say whose an address is: an **administrator's entry** (the invitation is made for an address: `user invite --email`, or the Users page; `user set-email` or the Set button changes it), the **user principal name the domain issued**
(never a `DOMAIN\user` name, which is not an address and is never guessed into one: a Windows account with no UPN signs in by its SID if it already has an account, and is refused as a new one), and the identity home's **verified** id token
(`email_verified`; without it nothing is linked). What a person types when redeeming an invitation is never used (only the first-run invitation, which nobody made, keeps it, for display). An address nobody vouched for, including every
address of an account made before this slice, is shown (marked unverified) and never links; those accounts keep signing in as they did, and an administrator sets their address to link them.

## Servers

**Signing in through an identity home** (plan 13a U14): `server run --identity-home https://home.example --identity-client <id> --identity-secret-env HOME_SECRET [--identity-app console]` makes the console an OpenID Connect client of the home. It keeps no passwords or passkeys and writes no first-run invitation; people sign in at `/api/identity/sign-in`, are the same user id here as everywhere, may sign in only if the home gives them a role on `app:<identity-app>` (`admin` makes them an administrator here), and reach each server with the roles the home holds for them there, never above the ceiling this console was paired with. A console with a home **keeps local users** (the way in when the home is missing): local sign-in is not closed, and a local session is never refreshed from or ended at the home.

Inside a session the home stays the authority (plan 13a U17). Each session holds a refresh token (kept protected in the console's state database, never as the home gave it). The first request after the **refresh interval** (5 minutes) reads the person's roles at the home again, so a role removed there applies within the interval without a new sign-in, and the console's ceiling for each server lowers them exactly as at sign-in; a person the home no longer gives a role on `app:<identity-app>`, a disabled person, or a sign-in the home ended is signed out of the console. When the home cannot be asked, the last roles are kept for a **grace** of 10 more minutes, then the person holds **no role on any server** (the servers refuse the call) until the home answers; their sign-in to the console itself stays. Signing out of the console ends the person's session at the home (OpenID Connect RP-Initiated Logout: the sign-out answer carries `redirectTo`, the home's end-session address, and the web app goes there), which also ends their other consoles' sessions at their next refresh. The interval and the grace are not flags yet. The home must register the console's `post_logout_redirect_uri` (`<public address>/`) for the browser to be brought back; without it the home shows its own page.

`dbdatakit instance add` pairs the console with a server (Sync, Proxy, Build or Store) using the single-use code that server's administrator made with `<program> server pair`; the server's certificate is pinned and the pairing is recorded in `instances.yaml`
in the console's directory. `instance list` shows each server with its product, version and whether it answers, `instance remove` forgets one (it keeps listing the console until `server unpair` is run there) and `instance rotate-key` moves every server to a new signing key.
A server started with `DBDATAKIT_CONSOLE_ANNOUNCE_URL` (and `DBDATAKIT_ADVERTISE_URL`, `DBDATAKIT_INSTANCE_NAME`) and not yet paired announces itself to the console's private `--announce-listen` port and waits: `instance pending` lists the waiting servers (never their codes),
`instance confirm <id or name>` pairs with one and `instance reject` forgets it. In the web app the Servers page shows the same list above the paired servers, for administrators only: each waiting server with its name, product, advertised address and when it last announced,
and a Confirm and a Reject button. The list refreshes every few seconds, Confirm moves the server into the list below (a failure, such as the server not answering or its code having expired, is shown under that server in the console's own words) and Reject removes it until it announces again (every half minute while it waits).
In the web app, `/instances` lists them and `/servers/<id>/...` opens a server's own screens (Sync's replications and configuration so far), whose calls the console relays at `/relay/<server>/api/...` and `/relay/<server>/hubs/...`.

## What is served

`/api/auth`, `/api/invites` and `/api/users` are the shared sign-in controllers of `DbDataKit.Web` (the same ones Sync serves), `/health` and `/version` answer without signing in, and everything else under `/api` is closed
to anyone who is not an administrator unless an endpoint says otherwise. The web app is the build of `@dbdatakit/console` in `wwwroot` beside the program; without it the console serves the API and says the app is not installed.
