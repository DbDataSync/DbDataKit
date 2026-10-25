# dbdatakit, the console

The one web interface for the DbData servers (plan 13, U07). It signs people in, holds the users, and (in the slices that follow) manages the Sync, Proxy and Build servers paired with it.
It is a separate host from the servers: a server runs without it, and it is not needed to run one.

```
dbdatakit server run [--repo <console directory>] [--listen https://localhost:8480]
dbdatakit user list [--repo <console directory>]
dbdatakit user invite [--role admin|viewer] [--user <id>] [--url <address>]
dbdatakit service install|uninstall|status|start|stop|restart|doctor|unit
dbdatakit health check [--url <url>]
```

The console's directory is `--repo`, or `DBDATAKIT_CONSOLE_DIR`, or a folder under the user's local application data (`%ProgramData%\DbDataKit\console` for the Windows service). It holds `state.db`
(users, sessions, invitations, notifications), the self-signed certificate (`tls.pfx` and its password file, readable only by the account that runs the console) and `FIRST-RUN.txt`.

## First run

With no user yet, the console writes an invitation to `FIRST-RUN.txt` and the log. Open `https://localhost:8480/invite#<code>` once and register a passkey: that person is the first administrator.
`dbdatakit user invite` prints a fresh invitation when that one has expired or scrolled away (it opens the state database directly, so it works when nobody can sign in).

## What is served

`/api/auth`, `/api/invites` and `/api/users` are the shared sign-in controllers of `DbDataKit.Web` (the same ones Sync serves), `/health` and `/version` answer without signing in, and everything else under `/api` is closed
to anyone who is not an administrator unless an endpoint says otherwise. The web app is the build of `@dbdatakit/console` in `wwwroot` beside the program; without it the console serves the API and says the app is not installed.
