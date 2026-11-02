# Plan 27: DbDataKit Studio, trust lists, and the `dbdatakit` tool

Status: **proposed** (2026-11-02). Replaces the draft `27-draft-admin-cli-adcs-push.md`, which was written against an old checkout and is deleted by
the change that adds this file. The slices are in [13a](13a-suite-slices.md), lane I.

## 1. What this plan does

One goal: get certificates from the company's Active Directory Certificate Services (AD CS) onto servers that run on Linux, without
anyone copying private keys around by hand and without cutting off the tool that manages those servers. It takes four connected changes:

1. **A name.** The web host that signs people in, holds the users and manages the servers stops being called "the console". It becomes
   **DbDataKit Studio**. ("Console" also means the terminal in this code base, so it was ambiguous everywhere.)
2. **Trust lists.** A paired server's certificate is today remembered as one fingerprint (a "pin"). It becomes a **trust list** of one or
   more fingerprints, so a certificate can be replaced while the old one still works.
3. **A tool.** A new command, **`dbdatakit`**, shipped as a dotnet tool, for administrative work: first certificates, later more. It signs
   in interactively and finds what it manages from a config file in the current directory tree.
4. **Certificates.** On a Windows workstation, the tool asks AD CS for a certificate, sends it to the server through Studio, and replaces
   the old one without a gap. A Linux-native request is a later step behind the same interface.

Everything here is a **clean break**: no migration, import or compatibility reader anywhere (plan 13 section 3). Section 4 lists each
break and how the old form fails.

## 2. Names

| Term | Means | Was |
| --- | --- | --- |
| **DbDataKit Studio**, "Studio" | the web host: sign-in, users, invitations, the servers it manages and their screens, every module's docs | "the console", `DbDataConsole` |
| `dbdatakit-studio` | the command: Studio's executable and command line (`server run`, `user`, `instance`, `identity`, `service`, `health`), its release tags, container image, installers and systemd unit | `dbdatakit` |
| `DbDataKit.Studio` | the identity in code: its folder `DbDataKit.Studio/`, projects and packages `DbDataKit.Studio.*`, namespaces, and the Windows service name | `DbDataConsole`, `DbDataConsole.*`, service `DbDataKitConsole` |
| **`dbdatakit`** | the new administrative tool (a dotnet tool, section 6); `local` approval is in its first version | new |
| **server** | a DbDataSync, DbDataProxy, DbDataBuild or DbDataStore server that Studio manages | unchanged |
| **trust list** | the certificate fingerprints a client accepts for a server (section 5) | "pin" |
| **identity home** | the OpenID Connect provider people can sign in through | unchanged |
| **console** | now only the terminal (`IConsole`, `SystemConsole`) | |

Studio is not a branded product like Sync or Proxy; it is the umbrella the products are managed from. That is why it is "DbDataKit
Studio" and not "DbDataStudio".

**Two spellings, and only two.** Every product here already follows one rule, and Studio follows it too: a dotted or capitalised
**identity** for anything in the source tree, and a lower-case **command** for anything you type or that is named after the program.

| | Identity (the source tree, the service) | Command (typed, released) |
| --- | --- | --- |
| Sync | `DbDataSync/`, `DbDataSync.*`, service `DbDataSync` | `dbdatasync` |
| Studio | `DbDataKit.Studio/`, `DbDataKit.Studio.*`, service `DbDataKit.Studio` | `dbdatakit-studio` |
| the admin tool | its project and package (name open, decision 2) | `dbdatakit` |

The hyphen in `dbdatakit-studio` is only there because the command `dbdatakit` is taken by the admin tool.

## 3. What exists today (read from the code, 2026-11-02)

- **Pairing and relay.** A server makes a single-use pairing code (`server pair`). Studio pairs with it (`instance add`), remembers the
  server in `instances.yaml`, signs a short-lived token per call naming the person and their roles, and relays the browser's calls through
  `/relay/<server>/api/...`. Servers check the token and decide what the role may do. A paired client has a *kind*: `Console`, `Server` or
  `Cli`.
- **The pin.** The pin is the SHA-256 of the server certificate's whole DER encoding, one string per server (`PairingProof.CertificateFingerprint`;
  `fingerprint:` in `instances.yaml`; `Fingerprint` in Build's record of a Proxy). The check accepts a certificate whose fingerprint equals it
  and ignores the chain, the host name and the dates (`PairingClient.PinnedSocket`). Every call uses it: the relay (`ConsoleRelay`, HTTP and
  WebSocket), status, key rotation, and Build's publisher calls. A renewed certificate has a new fingerprint, so the link breaks until the
  server is paired again with a new code.
- **Certificate endpoints differ per server.** Proxy: `GET /api/v1/certificate`, `POST /api/v1/certificate/renew` (self-signed only).
  Sync: `api/admin/certificate` (its own shape). Store and Build: not checked. Plan 13 says certificate changes need the Admin role.
- **Sign-in.** Studio's shared sign-in controllers (`DbDataKit.Web`: `/api/auth`, `/api/invites`, `/api/users`) offer passkeys (after an
  invitation), Windows sign-in (Negotiate) and OpenID Connect through an identity home. A session is a row in the shared `SessionStore`
  (12 hours, not sliding); the cookie is an opaque id and the role is read from the user row on every request.
- **Command framework.** `DbDataKit.Cli` (`Noun`, `Verb`, `EffectClass`, one JSON document per command with a schema per verb) is what every
  product's command line uses. Tools already exist as dotnet tools (`DbDataKit.Update.Tools` → `dbdata-update`; `DbDataSync.Cli` → `dbdatasync`).
- **AD CS code.** `DbDataKit.Hosting.Certificates.Windows`: `AdcsEnrollment` (the `CertificateAuthority.Request` COM object, so Windows
  only), `CaDiscovery`, `CertificateTemplateCatalog`, `PendingEnrollmentStore`, `CertificateBuilder`. It ran inside standalone DbDataSync;
  since it moved into the shared package it has not run against a real CA (plan 12 follow-up 9).
- **The CAs.** Both enterprise CAs accept RPC/DCOM enrollment. The new CA publishes nothing else (no CES, HTTPS not listening). The legacy
  CA publishes CES and `/certsrv`, but its CES endpoint answers 503. So the only path that works against the new CA is RPC/DCOM from
  Windows, which is why a Windows workstation does the requesting.

## 4. The clean break

Nothing is migrated. Each old form fails with a message that names the file or variable and says what to do, and a test pins the message.

| What | Old | New | How the old form fails |
| --- | --- | --- | --- |
| Executable, service, release | `dbdatakit`, service `DbDataKitConsole`, tags `dbdatakit/v…`, image and installers `dbdatakit`, stable URLs `…/dbdatakit/dist/…` | `dbdatakit-studio`, service `DbDataKit.Studio`, tags `dbdatakit-studio/v…`, and so on | The docs checker (`scripts/check_docs_commands.py`) rejects every old command form in docs, scripts and workflows. An installed old service is untouched; uninstall it first (release note). |
| Folders and projects | `DbDataConsole/`, `DbDataConsole.Server`, `DbDataKit/web/console`, `@dbdatakit/console` | `DbDataKit.Studio/`, `DbDataKit.Studio.Server`, `DbDataKit/web/studio`, `@dbdatakit/studio` | Does not build against the old names. |
| Data directory and environment | `DBDATAKIT_CONSOLE_*`, default directory `…/DbDataKit/console` | `DBDATAKIT_STUDIO_*`, default `…/DbDataKit/studio` | Studio starts with an **empty** directory (no users, no certificate, a new first-run invitation). It refuses to start when an old-named variable is set, and names it. |
| Wire values | `ClientKind.Console` (the lower-case word is inside the pairing proof), proof labels `dbdata-pair-v1-*`, management API version 1 | `ClientKind.Studio`, labels `v2`, management API version 2 with the supported floor raised to 2 for this step (the usual rule also accepts the version before) | A version-1 server is reported as "Upgrade this server". A `server.yaml` listing a client of kind `console` is refused, naming the file. |
| Signing key | secret `kit/console/signing-key` | `kit/studio/signing-key`, a new key | The old key is not read. With the wire change every server is paired again anyway. |
| Trust | `fingerprint:` in `instances.yaml`; `Fingerprint` in Build's paired Proxy record | `trust:` (a list) | A file or record with `fingerprint:` is refused, naming the file: "pair again". |
| Reserved app path | `console` | `studio` | An app manifest naming `console` is no longer rejected for that reason; `studio` is. |
| Docs checker | `PRODUCTS` maps `dbdatakit` to `DbDataConsole`; `RETIRED` knows nothing of it | `dbdatakit-studio` maps to Studio, `dbdatakit` to the new tool; `RETIRED` lists `dbdatakit server|instance|user|identity|service` → `dbdatakit-studio …` | Any doc that still types the old commands fails the check. |

Unchanged: `IConsole` and the other terminal types; `DBDATAKIT_*` variables that are not about the console (`DBDATAKIT_ADVERTISE_URL`,
`DBDATAKIT_INSTANCE_NAME`, `DBDATAKIT_IDENTITY_*`); `X-DbData-Token`; the library and app "pins" (`Apps.Instances`, scripting lock files),
which are unrelated.

What a person does after upgrading: install Studio, sign in with the new first-run invitation, run `server pair` on each server, and
`dbdatakit-studio instance add` for each (or let the servers announce themselves). That is the whole procedure; it goes in the release
notes.

## 5. Trust lists (replacing the pin)

**Model.** Each server Studio manages has a list of accepted certificate fingerprints, each with the date it was added. A connection is
accepted when the certificate it presents has a fingerprint in the list (same SHA-256 of the DER bytes as today; still no chain, name or
date check, so an expired certificate that is on the list still works). The first entry is made by pairing, exactly as the pin is today,
including the proof that binds the code to the certificate seen. Build's record of a Proxy gets the same list. The tool uses the same idea
for the Studio it talks to (section 6).

**Rollover.** To replace a server's certificate without a gap:

1. The new certificate is **staged** on the server (it is stored, not served). The server answers with the new fingerprint. That answer
   arrives over the connection the old entry already authenticates, so the new fingerprint is vouched for by the old one, the same idea as
   key rotation.
2. Studio **adds** the fingerprint to the list.
3. The server **activates** the staged certificate (swapping it in, or restarting if it must).
4. Studio connects and checks which entry the server presented. Only if it is the new one is the rollover **verified**.
5. Studio **removes** the old entry (decided: this is the default; `--keep-old` leaves it).

If activation fails and the server falls back to the old certificate, the old entry is still listed, so nothing is lost; the command says
what happened and leaves both entries. A list with two entries for more than a set time (a week) is shown as a warning, because a retired
certificate whose key leaks would still be accepted.

**Visible.** `instance list` and the web app show each server's entries, which one was seen last, and when each was added. New verbs:
`instance trust list|add|remove|verify`, and the matching calls under `api/instances/{id}/trust`.

**Not in this plan.** Trusting a CA (any certificate it issued) would remove rollovers entirely for AD CS certificates. `HomeTrust`
already does this for the identity home. It can be added later as another kind of list entry.

## 6. The `dbdatakit` tool

### 6.1 Packaging

- A project under `DbDataKit/src/`, `PackAsTool`, `ToolCommandName` `dbdatakit`, installed with `dotnet tool install -g <package id>`, the same
  pattern as `DbDataKit.Update.Tools`. The project and package name is open (decision 2): `DbDataKit.Cli` is preferred, but that name is the
  shared command framework today. One build for Windows and Linux.
- Built on `DbDataKit.Cli` (nouns and verbs, `--format json`, a data schema per verb). The docs checker reads its nouns from the source.
- Exit codes: 0 success, 1 failure, 2 usage, 3 not signed in.
- Windows-only verbs (`cert issue`, `cert discover`, `cert templates`) check the operating system and say plainly what to run where.

### 6.2 Finding Studio and the server

The tool talks to **Studio**; Studio names the servers.

- **File:** `dbdatakit.yml`, found by walking up from the current directory. `--config <file>` names one. A user-level file
  (`%APPDATA%\DbDataKit\dbdatakit.yml`, `~/.config/dbdatakit/dbdatakit.yml`) is merged underneath, the nearer file winning per key. No tool
  here walks up the tree yet; this follows the git and `global.json` convention.
- **Studio, first match wins:** `--studio <name-or-url>`; the `DBDATAKIT_STUDIO_URL` variable (the one servers already use to say where
  Studio is); `default:` in the file; the only entry in the file; otherwise an error that lists the names. A URL with no file works.
- **Server:** `--server <name-or-id>`, a name Studio lists (`dbdatakit server list`). A command that needs one and does not get it lists
  the choices.

```yaml
# dbdatakit.yml  (no secrets: safe to commit)
default: main
studios:
  main:
    url: https://studio.corp.example.com:8480
    trust: [3f9a…c1d2]            # fingerprints accepted for Studio's own certificate; absent = the system's trust
servers:                          # per server, by the name Studio lists: what the tool does for it
  sync-prod:
    certificate:
      dns: [sync.corp.example.com, sync]
      ca: ca01.corp.example.com\Corp Issuing CA
      template: CorpWebServer
```

First contact with a Studio certificate the system does not trust: the tool shows its subject, issuer, dates and fingerprint and asks
whether to trust it (like an SSH host key); a script passes `--trust <fingerprint>`. It is saved in the user-level file only when the person
says so. There is no "skip verification" flag.

### 6.3 Signing in

`dbdatakit login [--studio …] [--method auto|browser|code|local|windows]`, `logout`, `whoami`. The tool signs in to **Studio**, as a
person, and uses that person's session. It never holds a credential of its own for a server.

| Method | What happens | When |
| --- | --- | --- |
| `browser` (default) | The tool prints a link and a short code and opens the browser when it can. The person signs in as usual (passkey, Windows, the identity home), checks the code, the requesting address and the tool's label, and approves. The tool polls and receives a session. | anywhere |
| `code` | The same, never opening a browser. `auto` chooses it over ssh or without a display. | remote shells |
| `local` | Terminal only. The tool prints the code; someone on the Studio host runs `dbdatakit-studio user approve --code <code> --user <id>`, which writes to the state database the way `user invite` does. | no browser reaches Studio |
| `windows` | Terminal only. The tool negotiates with the workstation's signed-in identity against the existing `/api/auth/windows`. | where Studio offers Windows sign-in |

New endpoints, in the shared sign-in controllers (so any host that has them has this):

- `POST /api/auth/cli/start` (anonymous, rate limited) returns a 256-bit device code, an 8-character user code, the approval link, an
  expiry (10 minutes) and a polling interval. The request records the caller's address and a label such as `dbdatakit on WS-ADMIN01`.
- The approval page (`/cli-approve?code=…` in Studio's web app) needs a signed-in person, shows the code, the label, the requesting address
  and Studio's own certificate fingerprint (to compare with the one the tool prints when the certificate is self-signed), and offers Approve
  and Deny. Wrong codes are limited and say nothing about other codes.
- `POST /api/auth/cli/poll` (anonymous, rate limited) returns pending, denied, expired, or approved with a session, once.
- The session is an ordinary `SessionStore` session for the **approving person**, with their roles, labelled `cli` in the audit. It lasts
  the shared lifetime (12 hours, not sliding). The tool sends it as the session cookie, so every existing policy works unchanged.

The tool caches the session per Studio, protected (DPAPI on Windows through `DbDataKit.Secrets`; a 0600 file elsewhere). A `401` becomes
"not signed in to <Studio>; run `dbdatakit login`" with exit code 3. Nothing re-logs in silently.

### 6.4 What it talks to

| Target | Address (default) | Used for |
| --- | --- | --- |
| **Studio** | `https://…:8480` | sign-in (`/api/auth/cli/*`, `/api/auth/windows`), the approval page (in a browser), `/api/instances` and its trust calls, and `/relay/<server>/…` |
| **Servers** (Sync, Proxy, Build, Store) | only through Studio's relay | certificate status, stage, activate (Admin role) |
| **Identity home** | only in the browser, during sign-in | |
| **AD CS** (Windows workstation only) | the CA over RPC/DCOM; a domain controller over LDAP and Kerberos | issuing and discovery |

No server needs to be reachable from the workstation; only Studio and, for issuing, the CA and domain controller.

### 6.5 Commands in the first version

| Verb | Does |
| --- | --- |
| `login`, `logout`, `whoami` | section 6.3 |
| `studio show` | what the config resolved to, from which files |
| `server list`, `server status [--server]` | the servers Studio manages and how they answer |
| `trust list|add|remove|verify --server` | a server's trust list (section 5) |
| `cert status --server` | the certificate the server presents, its days left, whether a staged one waits |
| `cert discover`, `cert templates` | Windows: the CAs and templates in Active Directory |
| `cert issue --server --dns … [--ca] [--template]` | Windows: request, stage, trust, activate, verify, clean up (section 7) |
| `cert push --server --pfx <file>` | the same from a PFX you already have |
| `cert retrieve --request-id <n>` | finish a request that needed approval |
| `cert sync` | every server in the file inside its renewal window: issue and roll over |
| `doctor [--server]` | section 7.4 |

Later, once this is in: remote `user invite`, `config check`, `update status`. Folding the other command lines in as `dbdatakit sync …`
is possible and nothing here prevents it.

## 7. Certificates

### 7.1 The flow

```
 Windows workstation (admin's account)                       Studio                    Server (Linux)
 -------------------------------------                       ------                    --------------
 dbdatakit login ------- device-code approval ------------>  session
 discover CA and template  (LDAP, Kerberos)
 ephemeral RSA key + CSR
 submit to the CA over RPC/DCOM  ----> CA
                                 <---- issued certificate (or a request id: pending approval)
 PFX in memory, AES-256, one-time passphrase
 cert stage  ----------------------> relay (token as person) -----> validate, store, report fingerprint
                                     trust: add fingerprint
 cert activate  -------------------> relay ----------------------> serve the new certificate
                                     connect: which entry seen?
                                     trust: remove the old entry
 delete the key and the pending record
```

### 7.2 Workstation side (Windows)

In `DbDataKit.Hosting.Certificates.Windows`:

- `AdcsPfxIssuer`: an in-memory RSA key (3072 bits unless the template needs more), the CSR from `CertificateBuilder`, the submit, and on
  *issued* a PKCS#12 with the leaf, the chain the CA returned and the key. The CA sees only the public key, so the template's "allow
  private key export" flag does not apply to a CSR the client made.
- `ICaSubmitter` in front of `AdcsEnrollment.Submit`/`Retrieve` so the logic is testable on Linux CI; the COM call stays untested in CI.
- A request that needs manager approval is *pending*. The key must survive until `cert retrieve`, so `PendingEnrollmentStore` gains an
  optional key blob protected with DPAPI `CurrentUser`, deleted on success, denial, or after a sweep (14 days).

The template must have: the serverAuth EKU; "Supply in the request" for subject and subject alternative names (a template that builds the
name from AD would name the workstation); Enroll permission for the admin group that runs this; and no manager approval, or the pending flow
is accepted. Slice I00 records the real settings.

### 7.3 Server side (every server, one contract)

Today each server differs (section 3). Plan 27 gives them one contract under the management API, Admin role only:

- `GET /api/v1/certificate`: what is served (subject, names, fingerprint, dates, days left, source), whether one is **staged**, and
  whether a restart is needed.
- `POST /api/v1/certificate/stage`: a PFX and its passphrase. The server validates, then stores the certificate re-protected with a fresh
  random password (`ProtectedPasswordFile`) and answers with its fingerprint. Serving does not change.
- `POST /api/v1/certificate/activate` with the staged fingerprint: serves it. Proxy swaps in-process (its `TlsCertificateHolder`
  already does); a server whose listener reads the certificate once at start reports "restart needed" and restarts only when told to.
- `DELETE /api/v1/certificate/stage`: discards it.

`CertificateImporter` (shared, `Hosting.Certificates`) validates before anything is written: the key matches the certificate; it is valid
now; it carries serverAuth; its names cover the server's configured names. A chain that does not build to a trusted root is a warning,
not a failure, because the enterprise root may not be in the server's store. Replacement is atomic and keeps the previous certificate. On
start, a current certificate that will not load makes the server fall back to the previous one, with a loud log line and a degraded status;
it never falls back to plain HTTP.

No response, log line or audit entry contains key material or the passphrase (the existing sentinel tests are extended).

### 7.4 Setup, broken setups, expired certificates

**Initial setup.** A fresh server has a certificate of its own (self-signed), and pairing trusted it. Studio's first administrator comes from
its first-run invitation (a browser, because a passkey needs one). Then `dbdatakit login`, `cert issue --server …`, and the rollover moves
the server from its self-signed certificate to the AD CS one with no gap.

**`dbdatakit doctor`** walks a ladder and stops at the first failure with a cause and the exact command for the host when only the host
can repair it:

1. Configuration: which file, which Studio, which server.
2. Reach Studio: DNS, TCP, then TLS (trusted, untrusted with its fingerprint, expired, wrong name, plain HTTP on a TLS port).
3. Sign-in: which methods Studio offers, whether a session is cached and valid, which roles.
4. Each server: reachable through the relay; the trust list matches what it presents (and, if not, says "its certificate is not on the
   trust list; pair again or add the new fingerprint"); health; certificate days left; a staged certificate waiting.

The tool cannot repair a server that does not answer. The commands on the host (`dbdatasync cert use-pfx`, `dbdataproxy cert …`) stay,
and `doctor` prints the right one.

**Expired.** A server's expired certificate keeps working through Studio as long as it is on the list, because trust is by fingerprint, not
by date; staging and rolling over is how it is fixed. If *Studio's own* certificate expired, the tool shows the fingerprint and asks, or
takes `--trust <fingerprint>`; the approval page in the browser shows a certificate warning that the person can click through after
comparing fingerprints, or `local` avoids the browser. Studio's own certificate is renewed on the Studio host (`dbdatakit-studio cert …`);
managing it from the tool is not in this plan.

**Warnings before expiry.** `cert status` and `doctor` show days left; `cert sync` renews inside the window (30 days, the shared
`RenewBeforeDays`). Whether a server on Linux warns about a *file* certificate that nobody renews is checked in slice I05; if not, it is fixed
there.

## 8. Slices

Detail and "Done when" are in [13a](13a-suite-slices.md), lane I. Order (the **Needs** column is what matters):

| Slice | What | Needs |
| --- | --- | --- |
| I00 | smoke test of the existing AD CS enroller against both real CAs (manual) | |
| I01 | trust list in the shared registry, relay, status, key rotation and Build's publisher | |
| I02 | rename: console → Studio, everywhere, including the wire values (section 4) | I01 |
| I03 | `CertificateImporter` (shared) | |
| I04 | the certificate contract (section 7.3) on Proxy | I03 |
| I05 | the same contract on Sync | I04 |
| I06 | the same contract on Store and Build | I04 |
| I07 | the `dbdatakit` tool: project, packaging, config discovery, Studio and server selection | I02 |
| I08 | sign-in: endpoints, approval page, `local` approval, client, session cache | I07 |
| I09 | `server` and `trust` verbs; Studio's trust calls | I01, I08 |
| I10 | AD CS issuer on Windows | I00 |
| I11 | `cert` verbs and the rollover | I04, I09, I10 |
| I12 | `cert sync` and `doctor` | I11 |
| I13 | Linux-capable CA and template discovery (LDAP with Kerberos) | I07 |
| I14 | a Linux enroller (CES or `certmonger`) | I11, and the CA team publishing CES |

I00 can start now. I01 and I03 can run in parallel; I02 waits for I01 so the two do not collide in the same files.

## 9. Risks and open points

- **The key passes through Studio.** The relay carries the PFX and its passphrase in memory on their way to the server. Studio is already
  trusted with every relayed call, and nothing may log bodies, but it is new exposure. A direct path (`ClientKind.Cli` already exists for "a
  command line on someone's machine") would avoid it, at the cost of a second credential model. Not in this plan; revisit if it matters.
- **Real-CA behaviour is unproven** since the move (I00). COM dispositions differ by CA configuration.
- **A wrong template is silent at the CA.** I03's validation rejects a certificate with the wrong names; I00 documents the template.
- **Device-code consent phishing.** Someone could get a person to approve a code. The page shows the label and requesting address, codes
  last 10 minutes, and the session is the approver's own roles, nothing more.
- **Unattended renewal.** A scheduled `cert sync` cannot approve a sign-in, and sessions last 12 hours. For now a person runs it. A service
  identity is a later design.
- **Pending requests** keep an encrypted key on the workstation for days; the sweep is 14 days.
- **Restart on activation** interrupts a server that must restart (Sync's scheduler); I05 checks what an in-flight run does.
- **A forgotten old trust entry** stays valid; the one-week warning is the only guard.
- **The CA team.** I14 needs Kerberos-authenticated CES on the new CA and Enroll permission for the Linux machines' computer accounts. A client
  for the legacy CA is not worth building: its CES returns 503 and the server may be retired.

## 10. Decisions for the owner

Decided: the folder is `DbDataKit.Studio/`; `local` approval is in the first version; the rollover removes the old trust entry by default.

1. **`instance` or `server`** for a managed product, in Studio's command line and in the tool's. Today Studio says `instance` (`instance add`,
   `instance list`, `/api/instances`, `instances.yaml`, `InstanceId`) while its own help text calls them "servers", because the shared
   `server` noun (`server run`, on every product, including Studio) already means "this program's own server". Recommendation: the tool
   says `instance` too, so the two command lines match; prose keeps saying "server".
2. **Name of the tool's project and package.** `DbDataKit.Cli` is preferred, but it is the shared command framework (namespace
   `DbDataKit.Cli` in 54 files across all six products, six projects reference it). Either that framework is renamed first (for example to
   `DbDataKit.Commands`, a mechanical slice before I07), or the tool takes another name.
