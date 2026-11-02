> **SUPERSEDED DRAFT, do not build from this.** Written against a checkout of `shared-tls-rollout-14-followups` (21 Oct 2026),
> 1,033 commits behind `main`. It predates the console (`dbdatakit server run`), pairing with pinned certificates, signed console
> tokens, the relay, Proxy's management listener and `DbDataKit.Cli`. Its sign-in design (device-code sessions on Sync) and its
> "Sync has its own sessions and invites" assumptions no longer hold. See `docs/suite-listeners-and-auth.md` for the current picture.
> Two things to resolve before a rewrite: the name `dbdatakit` is already the console executable, and replacing a server's
> certificate interacts with the console's certificate pin. The AD CS findings (new CA offers RPC/DCOM only; legacy CES returns 503)
> and the phase 0 smoke test remain valid.

# `dbdatakit`: an admin CLI, first use: AD CS certificates for Linux-hosted services

Status: proposed. Follows `12-shared-tls-rollout.md` (slices 4 and 5 moved the AD CS code into `DbDataKit.Hosting.Certificates.Windows`).

## Goals

1. A new command, **`dbdatakit`**, shipped as a **dotnet tool**, for administrative work against running DbData servers.
2. First job: get AD CS certificates onto domain-joined Linux hosts, by issuing on a Windows admin workstation and pushing.
3. It signs in **interactively**, entirely in the terminal or through a link opened in a browser.
4. It finds the servers it works with from a **config file in the current directory hierarchy**, like the other development
   tools, and takes the server as a **parameter** when asked.
5. Later: other admin and setup work that fits (user invitations, config checks, update status), and possibly folding the
   other CLIs in as `dbdatakit sync ...` / `dbdatakit build ...`. Not in this plan, but nothing here may block it.

## Why this shape

AD CS enrollment is Windows-only today (`AdcsEnrollment` calls the `CertificateAuthority.Request` COM object; `CaDiscovery`
and `CertificateTemplateCatalog` are Windows-only too). What the real CAs offer:

| | Legacy CA | New CA |
|---|---|---|
| RPC/DCOM enrollment (what `AdcsEnrollment` uses) | works | works |
| CES published (`msPKI-Enrollment-Servers`) | yes, but the endpoint returns HTTP 503 | no |
| `/certsrv` web enrollment | yes | HTTPS is not listening |

The only enrollment path that works against the new CA is RPC/DCOM from Windows. So a Windows workstation requests the
certificate and delivers it to the host. A Linux-native enroller (option B) stays a later phase behind the same seam,
gated on the CA publishing CES.

Accepted trade-off: the private key is generated on the workstation, so it exists on two machines. It may exist only while
being issued, transmitted and imported, and must be encrypted in transit.

## Principles

- **Reuse the proven enrollment logic.** It ran in standalone DbDataSync; since the move into the shared package it has not
  run against a real CA (plan 12, follow-up 9). Phase 0 re-proves it.
- **The key lives only as long as the job.** The workstation generates an ephemeral key, never puts it in a certificate
  store, and deletes everything after a confirmed push.
- **A host never sends keys out.** `AdminCertificateService` states that no endpoint returns or exports a private key. The
  new endpoint only receives one.
- **The host re-protects what it receives.** The imported PFX is re-exported under a fresh random password kept in a
  `ProtectedPasswordFile`, as the managed self-signed certificate already is. No unencrypted key lands on disk.
- **One answer to "who is this".** The CLI gets an ordinary Sync session, not a second credential type.
- **One seam for issuing.** Option B plugs into `ICertificateEnroller`; the receiving side does not change.

## The tool

### Packaging

- New project `DbDataKit/src/DbDataKit.Cli`, `PackAsTool`, `ToolCommandName` `dbdatakit` (the same pattern as
  `DbDataKit.Update.Tools` → `dbdata-update` and `DbDataSync.Cli` → `dbdatasync`). Install:
  `dotnet tool install -g <PackageId>`. The package id is a decision (below).
- One portable build for Windows and Linux. Windows-only verbs (`cert issue`, `cert discover`, `cert templates`) check
  `OperatingSystem.IsWindows()` and say plainly what to run where, as `CertCommand` does today.
- Command parsing with System.CommandLine, as DbDataBuild does, so help, `--json` and exit codes behave the same way.
  Exit codes: 0 ok, 1 failed, 2 usage, 3 not signed in.
- Distribution is the dotnet tool only for now. `dist/README.md` describes the standard zip/MSI/Scoop/script set; joining
  it is a later decision, easy to add since the tool is framework-dependent.

### Finding the server

Nothing equivalent exists to copy: DbDataBuild takes `--project` (default `.`) and reads `dbdatabuild.yml` in that directory,
and DbDataSync resolves `--repo` or a machine-wide default. No existing tool walks up the tree, so this is new, small, and
follows the git / `global.json` convention.

- **File name:** `dbdatakit.yml`, read with `DbDataKit.Config.Yaml` (already in the repo).
- **Discovery:** the nearest `dbdatakit.yml` from the current directory upward, stopping at the filesystem root. `--config
  <file>` names one explicitly. If both a project file and a user-level file (`%APPDATA%\dbdatakit\dbdatakit.yml`,
  `~/.config/dbdatakit/dbdatakit.yml`) exist, the project file wins per key (servers merge by name).
- **Server choice, first match wins:**
  1. `--server <name-or-url>`: a name from the file, or a URL used as is.
  2. `DBDATAKIT_SERVER`.
  3. `default:` in the file.
  4. The only server in the file.
  5. Otherwise an error that lists the names.
- `--server` with a URL that the file does not know works with no config file at all.
- `dbdatakit servers` prints what was found, from which file, and which one a command would use.

```yaml
# dbdatakit.yml
default: sync-prod
certificate:                          # defaults for every server
  ca: ca01.corp.example.com\Corp Issuing CA
  template: CorpWebServer
servers:
  sync-prod:
    url: https://sync.corp.example.com
    certificate:
      dns: [sync.corp.example.com, sync]
  sync-test:
    url: https://sync-test.corp.example.com
    pin: sha256:3f9a...                # accept this certificate even if untrusted (bootstrap)
```

The file holds no secrets: no passwords, tokens or keys. It is safe to commit.

### Signing in

`dbdatakit login [--server ...] [--method auto|browser|code|windows|local]`, `logout`, `whoami`.

Sync authenticates by session cookie only (`SessionAuthenticationHandler`), and `POST /api/auth/windows` only works when the
host itself is Windows (`WindowsSignIn.Resolve` returns null otherwise). So for Linux hosts the CLI uses a device-code
style approval and the server mints the same kind of session a browser sign-in does.

| Method | What happens | When |
|---|---|---|
| `browser` (default) | CLI prints a link and a short code (and opens the browser when there is one). The admin opens it, signs in with whatever the console offers (passkey, Windows), checks the code and host shown, and approves. The CLI polls and gets a session. | Anywhere; the usual route to a Linux-hosted Sync |
| `code` | Same flow, never tries to open a browser; for ssh and headless boxes. `auto` picks this when `SSH_CONNECTION` is set or no display exists. | Remote shells |
| `local` | Terminal only. The CLI prints the code; an admin on the **host** runs `dbdatasync auth approve <code> --as <admin>`, which writes to the state database the way `dbdatasync invite` already does. | When no browser can reach the console |
| `windows` | Terminal only. The CLI negotiates with the workstation's logged-in identity against the existing `/api/auth/windows`. | Windows-hosted Sync with Windows auth configured |

Server side (Sync, new `CliAuthController`):

- `POST /api/auth/cli/start` (anonymous, rate limited): returns a 256-bit `deviceCode`, a short `userCode` (8 characters,
  unambiguous alphabet), the approval link, `expiresIn` (10 minutes) and the polling interval. The request records the
  caller's address and a client label such as `dbdatakit on WS-ADMIN01` for the approval page.
- The approval page is a route of the existing console (`/cli-approve?code=...`). It requires a signed-in **Admin**, shows
  the code, the label and the address, and offers Approve and Deny. A wrong code never reveals whether another one exists,
  and attempts are limited.
- `POST /api/auth/cli/poll` (anonymous, rate limited): `pending`, `denied`, `expired`, or `approved` with a session. The
  device code is single-use and the approved session is handed over once.
- The session is an ordinary `SessionStore` session for the approving user, with a 12-hour lifetime (fixed, as today) and the
  `cli` label so it is attributable in the audit log. The CLI sends it in the session cookie header, so
  `SessionAuthenticationHandler` and every policy work unchanged.

Client side:

- Sessions are cached per server in a user-level store, not in the project file: DPAPI-protected on Windows through
  `DbDataKit.Secrets`, a 0600 file elsewhere (the keyring backend is still a stub).
- A `401` becomes "not signed in to <server>; run `dbdatakit login`", exit code 3. No hidden re-login in scripts.
- TLS: the server certificate is verified. A `pin` in the file accepts exactly that certificate for bootstrapping a host
  still on a self-signed certificate. There is no "skip verification" flag.

### Commands in the first version

| Verb | Purpose |
|---|---|
| `servers`, `login`, `logout`, `whoami` | above |
| `cert status [--server]` | what the server is serving: subject, names, expiry, days left, source (the existing `GET api/admin/certificate`) |
| `cert discover`, `cert templates` | Windows: list CAs and templates from Active Directory |
| `cert issue --dns <names> [--ca] [--template] [--out file] [--no-push]` | Windows: enroll and push (or write an encrypted PFX with `--out`) |
| `cert push --pfx <file>` | push an existing PFX (prompts for its passphrase, or `--password-stdin`) |
| `cert retrieve --request-id <id>` | finish a request that was pending approval, then push |
| `cert sync` | for every server in the file that is inside its renewal window: issue, push, verify |

Candidates for later, once this is in: `users invite` (remote `invite`), `config check`, `update status`, `service status`.
Not in the first version.

## Certificate flow

```
 Windows workstation (domain-joined, admin's own account)             Linux host (domain-joined)
 ------------------------------------------------------             ---------------------------
 dbdatakit login  ------------------ device-code approval ---------->  session
 discover CA/template (LDAP)
 ephemeral RSA key + CSR (CertificateBuilder.CreateSigningRequest)
 AdcsEnrollment.Submit over RPC/DCOM  ---------->  CA
                                      <----------  issued cert (or pending request id)
 build PFX in memory, AES-256 PBES2, one-time passphrase
 POST /api/admin/certificate/import (HTTPS, session) ---------------> validate, re-protect, write, activate
 delete key material and pending record                              report new thumbprint and expiry
```

### Workstation side

New in `DbDataKit.Hosting.Certificates.Windows`:

- `AdcsPfxIssuer`: an in-memory RSA key (3072 bits unless the template requires more), the CSR from `CertificateBuilder`, the
  submit, and on `Issued` a PKCS#12 holding the leaf, the chain the CA returned, and the key. The CA only sees the public key,
  so the template's "allow private key export" flag does not apply to a client-generated CSR.
- A seam in front of `AdcsEnrollment.Submit` and `Retrieve` (`ICaSubmitter`) so the issuing logic is testable on Linux CI.
  The COM call stays behind it and stays untested in CI, as today.
- **Pending requests.** A template that needs manager approval returns `Pending`, and the ephemeral key must survive until
  `retrieve`. `PendingEnrollmentStore` gains an optional key blob protected with DPAPI `CurrentUser`, deleted on push, on
  denial, and by an expiry sweep. Today the store references a machine CNG key name instead.

**Template requirements** (confirm with the CA team; a wrong template yields a valid certificate for the wrong name): the
serverAuth EKU; "Supply in the request" for subject and SAN (a template that builds the name from AD would name the
workstation); Enroll permission for the admin group running this; no manager approval, or accept the `Pending` flow.

### Host side (any OS)

New in `DbDataKit.Hosting.Certificates` (shared, so Proxy gets it too): `CertificateImporter`.

- Input: PFX bytes plus passphrase.
- Validation, all before anything is written: the key matches the certificate; it is currently valid; it carries serverAuth;
  its SAN covers the host's configured names (for Sync, the console URL host). The chain result is a warning, not a failure,
  since the enterprise root may not be in the host's trust store.
- Write: atomic replace of the managed PFX with the previous file kept, then re-export under a new random password stored in
  the `ProtectedPasswordFile`, with the existing ACLs (`DbDataKit.Security.Acl`).
- Activation: Sync reads the file at startup, so the first version restarts the service (or stages and reports "restart
  needed", a decision below); hot reload is a follow-up.
- Entry points: `POST /api/admin/certificate/import` on Sync, `[Authorize(Policies.Admin)]` like its siblings in
  `AdminCertificateController`, and `dbdatasync config cert use-pfx --password-stdin` locally. Today `use-pfx` accepts only a
  PFX with no password and points Kestrel at it unprotected, so the encrypted route and the re-protection are new.

### Renewal

`dbdatakit cert sync` is safe to run repeatedly: for each server in the file it reads the certificate status, applies the
shared `CertificateExpiry.Evaluate` with `RenewBeforeDays` (default 30), and issues and pushes only those inside the window.
`dbdatakit.yml` is the fleet list, so there is no second file. Run it by hand or from a scheduled task on an admin server
under an account with Enroll rights and a session (a scheduled run cannot approve interactively; see open points). The
host's `CertificateExpiryService` keeps warning, so a missed run is visible.

## Phases

Each phase is done only when its test column passes.

| # | Phase | Test |
|---|-------|------|
| 0 | **Smoke test of the existing enroller against the real CAs.** Windows workstation, your own account: `dbdatasync config cert discover`, `templates`, one `enroll --dns <test name>` against the new CA and the legacy CA. Record the template settings that worked in `DbDataSync/docs/certificates-ad-cs-enroll.md`. Fix whatever the move broke. Independent of everything below. | Manual checklist |
| 1 | **Tool skeleton.** `DbDataKit.Cli` project and packaging; System.CommandLine host; config discovery, server resolution, `servers`, `whoami` stub; exit codes; `--json`. | Discovery tests (walk up, nearest wins, merge with user file, `--config`, `--server` name and URL, `DBDATAKIT_SERVER`, ambiguity error); a pack test that `dotnet tool install` from the local package runs `dbdatakit --version` |
| 2 | **Sign-in.** Sync `CliAuthController`, the approval page, `dbdatasync auth approve`; client `login`/`logout`/`whoami`, session cache, browser launch rules, 401 handling. | API tests: pending, approve, deny, expire, single-use device code, wrong and guessed codes, rate limits, non-admin cannot approve, resulting session works on an Admin endpoint and is attributable; client tests against a fake server |
| 3 | **Issuer.** `ICaSubmitter`, `AdcsPfxIssuer`, encrypted PFX builder, pending key blob with expiry sweep. | Fake-CA tests on Linux CI (issued, pending, denied, failed; PFX round trip; nothing left behind after each outcome); Windows CI for the DPAPI part |
| 4 | **Importer and endpoint.** `CertificateImporter`, Sync import endpoint, `use-pfx --password-stdin`. | Valid, expired, wrong key, wrong SAN, wrong EKU, corrupt PFX; atomic replace and rollback; no plaintext key or password on disk; ACLs; Admin required |
| 5 | **Cert verbs.** `cert status|discover|templates|issue|push|retrieve` in `dbdatakit`. | End-to-end against a test Sync host with a fake CA: issue, push, status shows the new thumbprint |
| 6 | **Fleet renewal.** `cert sync`. | Window logic and idempotence with fake hosts (the second run does nothing) |
| 7 | **Option B, gated on CES.** 7a, available now: cross-platform CA and template discovery by LDAP (`System.DirectoryServices.Protocols` with Kerberos from the domain-joined host), so `cert discover|templates` work from Linux. 7b, after the gate: `CesCertificateEnroller : ICertificateEnroller` (or a `certmonger` wrapper), so a Linux host can enroll and renew itself. | 7a: parser tests on captured LDAP results plus one live check. 7b: needs a CES endpoint, deferred |

What phase 7b needs from the CA team: Kerberos-authenticated CES on the new CA (`ADCS-Enroll-Web-Svc`, optionally CEP), and
Enroll permission on the template for the Linux machines' computer accounts. A client for the legacy CA's `/certsrv` or CES
is not worth building: that CES endpoint is returning 503 and the server may be retired.

## Risks and open points

- **Real-CA behaviour is unproven since the move** (phase 0). COM dispositions differ by CA configuration, and
  `AdcsEnrollment`'s constants are documented values, not verified against a live server.
- **Template mistakes are silent** at the CA. Phase 4 validation rejects a wrong name at import; the better defence is
  documenting the template in phase 0.
- **Proxy has no admin API and no sessions.** It authenticates Flight users by password hash and has no cookie sign-in. The
  first version targets Sync. For Proxy, either a local `cert import` (run over ssh) or a comparable small admin surface
  later; decide before phase 5 so the config file's `product:` field means something.
- **Unattended renewal conflicts with interactive sign-in.** A 12-hour session cannot be approved by a scheduler. Options:
  run `cert sync` interactively (a person approves, then it covers the whole file), or a later service identity
  (a Windows-auth session on Windows-hosted servers, or a longer-lived restricted credential). Not solved here.
- **Sessions in the CLI cache** are bearer credentials for 12 hours. DPAPI on Windows, 0600 elsewhere, and `logout`
  deletes the server-side session too.
- **Antiforgery and CORS on the new anonymous endpoints** need checking against the existing middleware before phase 2.
- **Workstation compromise** during a job exposes the key in memory; the window is the point of the design. Pending keys are
  DPAPI-protected and swept (suggest 14 days).
- **Restart on import** interrupts Sync's scheduler; check what an in-flight run does.

## Decisions for the owner

1. NuGet package id for the tool (`DbDataKit.Cli`, `dbdatakit`, or `DbDataKit.Tool`); the command name stays `dbdatakit`.
2. Does import restart the service itself, or stage and report "restart needed"?
3. Include the `local` sign-in method (approval on the host) in the first version, or only `browser`/`code`/`windows`?
4. Proxy: CLI-only import first, or an admin endpoint there too?
5. When the other CLIs fold in (`dbdatakit sync ...`), do the product commands become subcommands of this tool or stay separate
   binaries it dispatches to? Nothing in phases 1 to 6 depends on the answer.
