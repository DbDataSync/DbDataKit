# CLOUDDEV: a full development instance in a cloud session, with a preview and a development MCP

## Goal

A Claude Code session on this repository (in the cloud first, on a developer's machine the same way) builds and runs the four services (the console,
the Workspace, the Runner, the Gateway) from the checkout as one paired development instance, shows the console's web app in the harness's preview, and
offers the running instance's MCP endpoints to the session through a development-only MCP forwarder. Nothing here ships in a release.

## What exists today

- From a restored checkout the console builds and runs (checked in a cloud session, 2026-11-19): `scripts/web-install.sh`, `npm run build -w
  @dbdatakit/console` in `Shared/web` (about 30 s), `dotnet build DbDataKit/src/DbDataKit.Server` (about 55 s), then `dbdatakit server run --repo <dir>
  --listen https://localhost:8480 --web-root Shared/web/console/dist`: SQLite state, a self-signed certificate, `/` answers 200. The other three were not
  tried.
- Each service has `server run` with `--repo` and `--listen` (Workspace 8471, Runner 8495, Gateway 8815 to 8822 today; INSTALLER-9 and INSTALLER-10 move
  them into 7800 to 7899).
- Pairing today is by code, announcement or the bundle token; INSTALLER's design (`docs/install.md`) removes all three for a command the console generates.
- People sign in to the console with passkeys; agents with identity-home tokens (`ddk_...`, personal or a service account's) on the Gateway.
- The Gateway's `mcp` module is built; the console's MCP endpoint is AGENTACCESS-5.
- The harness: Docker is installed with its daemon stopped; a port is shown in the preview pane when the session reports it (not every client shows one);
  `.mcp.json` servers start with the session, alongside `SessionStart` hooks. The repository has no `.claude/settings.json` and no `.mcp.json`.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CLOUDDEV-1 | `scripts/dev-instance.sh prepare`: the web install and builds the four services and the console's web app need, through `scripts/build-slot.sh`, each step skipped when its output is newer than its inputs | none | S | a test in `scripts/tests` with fake builds: a second `prepare` runs nothing; by hand in a cloud session from a fresh checkout |
| CLOUDDEV-2 | `dev-instance.sh start|stop|status`: the four services as processes (or as CLOUDDEV-P2 says) with data folders under `.dev/` (gitignored), SQLite state, ports from `SuiteServices`, paired the way INSTALLER pairs them; a failed start prints the service's startup failure file | CLOUDDEV-1, INSTALLER-3, INSTALLER-9 | M | by hand in a cloud session: `start` gives four ready services, the console lists the three as paired; `stop` leaves no process |
| CLOUDDEV-3 | The development identity: on first `start`, a person and a service account in the instance's identity home, the account's token in `.dev/token` (0600), and the person's sign-in as CLOUDDEV-P3 says | CLOUDDEV-2, CLOUDDEV-P3 | S | the token reads the Gateway's `mcp` `tools/list`; the person opens the console's Home |
| CLOUDDEV-4 | The preview: `start` prints the console's address for the session to report as a preview port, and the console's public address is set to what the preview opens | CLOUDDEV-2 | S | by hand: the console's page in the preview of the Claude app, signed in |
| CLOUDDEV-5 | The development MCP forwarder: a stdio server in `.mcp.json` that forwards to the dev instance's MCP endpoints (the Gateway's `mcp`, the console's from AGENTACCESS-5) with `.dev/token`, trusts only the instance's own certificates, refuses any address that is not the dev instance, and starts the instance on its first call when it is not running | CLOUDDEV-3, AGENTACCESS-5 | M | a test drives the forwarder over stdio against a started instance: `tools/list` from both endpoints, a read, a change waiting for approval; a non-dev address is refused |
| CLOUDDEV-6 | Session start, as CLOUDDEV-P1 says | CLOUDDEV-1, CLOUDDEV-P1 | S | by hand: a new cloud session starts without waiting and finds the builds done within a few minutes; a resumed one rebuilds nothing |
| CLOUDDEV-7 | Docs: `docs/cloud-dev.md` (the loop, the instance, the preview, the forwarder, what to do when a step fails) and a pointer from `CLAUDE.md` | CLOUDDEV-5 | S | `check_doc_inputs.py` passes |

## Needs from other subjects

- INSTALLER-3 and INSTALLER-9: the startup sequence and the suite ports the instance uses; INSTALLER's pairing command, which replaces the bundle.
- AGENTACCESS-5: the console's MCP endpoint, the forwarder's second target.

## Proposed decisions

### CLOUDDEV-P1: session hook or environment setup script (awaiting the owner)

Option A: a `SessionStart` hook in `.claude/settings.json` that runs `dev-instance.sh prepare` in the background and returns at once: reviewed with the
code, the same in every environment and on developers' machines, run at every start and resume (cheap when nothing changed). Option B: the cloud
environment's setup script: runs once per fresh container before the session, outside the repository's review, in that one environment only. Scenario: a
task changes the web build; under A the next session builds the new way, under B the environment runs the old steps until someone edits it.
Recommendation: A, with the instance started by the forwarder on its first call, not by the hook (MCP servers start alongside the hook and cannot wait
for it).

### CLOUDDEV-P2: processes or Docker Compose (awaiting the owner)

Option A: the four services as processes from the build output: fastest edit-and-run, no image builds. Option B: `dist/compose`, extended to the four:
closer to an install, but each change rebuilds an image and the daemon is started by hand in a cloud session. Recommendation: A for the loop, B stays the
quickstart.

### CLOUDDEV-P3: how the person viewing the preview signs in (awaiting the owner)

Passkeys are bound to the host name the console is opened at; the preview opens it through another address, where a passkey may not register. Option A:
the person signs in by the device code of CERTIFICATES' open design "A command line that runs no server", approved locally. Option B: a development-only
sign-in that the console accepts only when started by `dev-instance.sh` on loopback, refused otherwise with a message. Option C: register the passkey at
the preview's address (the console's public address set to it, CLOUDDEV-4), if the preview keeps one stable address per session. Recommendation: try C
first (a spike in CLOUDDEV-4); A if it fails; B only if neither works.

### CLOUDDEV-P4: where the forwarder lives (awaiting the owner)

Option A: a .NET project beside the products (`Shared/src/DbDataKit.DevMcp`, not packaged), built by `prepare`. Option B: a script in `scripts/` (Python,
standard library only, like the repository checks). Recommendation: B: it only forwards JSON-RPC lines to HTTPS, and needs no build.

## Shared areas touched

`.claude/settings.json` (new), `.mcp.json` (new), `.gitignore` (`.dev/`), `scripts/` (the script and its test), `CLAUDE.md`, `docs/` (a new page).
