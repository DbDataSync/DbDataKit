# CERTIFICATES: enterprise certificates on user-facing endpoints, and self-renewing management certificates

## Goal

The console and the Gateway's user-facing endpoints can serve a certificate from the company's Active Directory Certificate Services, checked before it is
served, with the previous one kept, renewed during the day about a week before it expires while an administrator is notified and can verify the result.
The management ports keep their self-signed certificates; each server renews its own automatically, and the console and the server trust both the old
and the new certificate until the old one expires or an administrator removes it, so a renewal never breaks a link. A certificate that will not load
never turns a server into plain HTTP.

## What exists today

- Each server makes and renews its own managed self-signed certificate (`DbDataKit.Hosting.Certificates`: `SelfSigned`, `CertificateProvider`,
  `ProtectedPasswordFile`); the shared `cert` command has `status` and `renew` for it (`Shared/src/DbDataKit.Hosting.Cli/CertCommand.cs`).
- The console remembers one fingerprint per paired server's management certificate (a pin); the join library in `DbDataKit.Web` (`JoinedServer`, `JoinedConsole`) makes the pin go both ways: the console keeps each server's in `instances.yaml`, and each server keeps the console's in its group's settings (`JoinedConsoles`). Renewing a
  self-signed management certificate changes its fingerprint, so today the link breaks until the server is paired again.
- An AD CS enroller exists as a library and nothing exposes it: `DbDataKit.Hosting.Certificates.Windows` has CA discovery (`CaDiscovery`), the template
  catalogue, the request (`CertificateBuilder`, `AdcsEnrollment`, `AdcsCertificateEnroller`), pending requests (`PendingEnrollmentStore`,
  `EnrollmentCompletion`) and the store. Its commands lived in the deleted Sync server; its messages still name `dbdatasync config cert retrieve`, which no
  longer exists. It has not run against a real CA since it moved into the shared package.
- The design here comes from a 2026-11-02 proposal on an old branch (in git under the old plans folder's `needs-review/`). The owner dropped its rename of
  the console to "DbDataKit Studio" (2026-11-17); its command line is the open design below, where its diagnostic command is named `check`.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CERTIFICATES-1 | The AD CS enroller behind the shared `cert` command of the console and the Gateway, run on a domain-joined Windows host for its own user-facing endpoint: `cert discover`, `cert templates`, `cert enroll --template <name> [--name <dns name> ...]`, `cert retrieve` (a pending request); an issued certificate goes through CERTIFICATES-3 and CERTIFICATES-4. The enroller's messages name these commands. An `ICaSubmitter` seam so the logic is tested without a CA | none | M | tests with a fake CA on Linux CI: issued, pending then retrieved, denied, failed; nothing is left behind after each outcome; a unit test reads the message of a pending request and finds a command that exists; the Windows job covers the DPAPI-protected pending key |
| CERTIFICATES-2 | A check against both real enterprise CAs, from a Windows host with an administrator's own account: discover, templates, enroll and retrieve for a test name; the template settings that work (serverAuth, "supply in the request" for subject and names, Enroll permission, manager approval or not) are written down | CERTIFICATES-1 | S | by hand, because no CA is in CI: the checklist and its result are in `Shared/src/DbDataKit.Hosting.Certificates.Windows/README.md`; anything the move broke is fixed with a unit test |
| CERTIFICATES-3 | `CertificateImporter` in `DbDataKit.Hosting.Certificates`: validate a PFX before anything is written (the key matches, valid now, serverAuth, its names cover the endpoint's configured names; a chain that does not build is a warning), replace atomically keeping the previous one, re-protect with a fresh random password; at start, a current certificate that will not load falls back to the previous one with a loud log line and a degraded status, never plain HTTP | none | M | tests on Linux and Windows: valid, expired, wrong key, wrong names, wrong EKU, corrupt PFX, wrong passphrase; a failed import leaves the served certificate; no key or password in plain text on disk; the fallback at start is logged and reported |
| CERTIFICATES-4 | Replacing a user-facing certificate: stage (stored, not served), activate (swapped in process where the listener allows, else at the next restart, which is reported), check what is served, keep the previous one; on the console and the Gateway, from `cert` and from the console's server screen through the relay (administrator only); a staged certificate can be discarded (`DELETE /api/v1/certificate/stage`) | CERTIFICATES-3 | M | tests on the console and the Gateway: staging changes nothing served; discarding a staged certificate removes it and changes nothing served; activating swaps and reports the fingerprint; a bad PFX leaves the served certificate; a sentinel test finds no key or passphrase in any response, log line or audit entry |
| CERTIFICATES-5 | Trust lists for management certificates: the console's pin of each server and each server's pin of the console (`instances.yaml` and `JoinedConsoles`) become a list of fingerprints, each with the date it was added and its expiry; every link accepts any listed certificate; an entry stays until its certificate expires or an administrator removes it, and an expired entry is removed automatically; the console's server screen and `dbdatakit server trust list\|remove` show and change it, with which entry was seen last and when each was added; a pairing record that still holds a single `fingerprint:` is refused naming the file and saying "pair again"; docs and messages say "trust", not "pin", for certificates | none | M | tests: both of two listed certificates are accepted and an unlisted one refused on every path (relay over HTTP and WebSocket, status, key hand-over); a swap between two listed certificates never drops the link; an expired entry is removed on a fake clock; a removed entry is refused at once; the server list shows the entry seen last and each entry's date; a record with `fingerprint:` is refused with "pair again" naming the file |
| CERTIFICATES-6 | Automatic renewal of self-signed management certificates, during the day, about a week before expiry: the server makes the new certificate and stages it, reports its fingerprint over the link the old one authenticates, the console adds it to its list (and the server adds the console's new one to its own list when the console renews), the server activates it, the console confirms the new one is presented; both certificates stay listed on both sides until the old one expires; the administrator is notified that it happened and how to check it | CERTIFICATES-5, ALERTING-4 | M | end to end on a fake clock between the console and a Gateway, in both directions: renewed inside the window and only in daytime hours, the link never drops, both entries listed until the old one expires, then the old one removed, and a notification raised; a renewal whose activation fails keeps serving the old certificate and notifies |
| CERTIFICATES-7 | Renewal of an enterprise certificate on a user-facing endpoint: during the day, about a week before expiry, a host that can enroll itself (CERTIFICATES-1) requests the new certificate and replaces it (CERTIFICATES-4), and notifies the administrator to verify it; a host that cannot enroll notifies the administrator well before expiry instead; days left show in `cert status`, `service check` and the console | CERTIFICATES-1, CERTIFICATES-4, ALERTING-4 | M | a fake clock inside the window renews once and only in daytime hours (a second run does nothing); outside it nothing happens; the notification is raised in both cases |

## Needs from other subjects

- ALERTING-4: the notifications of a renewal and of a certificate near expiry.
- APPHOSTING-5: renewal of bound host names' certificates uses CERTIFICATES-3's importer and CERTIFICATES-7's window rather than a second mechanism.

## Open designs

Not scheduled. Each becomes tasks only when the owner schedules it.

### A command line that runs no server, for certificates

**What.** A person on a domain-joined Windows workstation acts as coordinator and certificate publisher for a service on Linux, which cannot reach the
enterprise CA. The command line signs in to the console as that person, makes the key in memory, requests the certificate from the CA, and sends the PFX
to the service through the console (CERTIFICATES-4's replacement), so no private key is copied by hand. It holds no credential of its own for a server and
needs to reach only the console and, for issuing, the CA and a domain controller; servers are reached through the console's relay.

**Why.** The CAs (below) accept requests only over RPC/DCOM from Windows, while the services that need a certificate may run on Linux.

**The CAs, as found on 2026-11-02.** Both enterprise CAs accept RPC/DCOM enrollment. The new CA publishes nothing else (no CES, HTTPS not listening). The
legacy CA publishes CES and `/certsrv`, but its CES answers 503 and the server may be retired, so a client for it is not worth building. The only path that
works today is RPC/DCOM from a domain-joined Windows host. The template must have the serverAuth EKU, "Supply in the request" for the subject and subject
alternative names (a template that builds the name from AD would name the workstation), Enroll permission for the administrators' group, and no manager
approval unless the pending flow is accepted (CERTIFICATES-2 records the real settings).

**The starting point: what the 2026-11-02 proposal had decided.** It was written for a console renamed "Studio" and a tool named `dbdatakit`; the owner
dropped the rename, and the rest is the starting point until the owner settles this design.

- *Packaging.* One build for Windows and Linux on the shared command framework (`DbDataKit.Cli`: nouns, verbs, `--format json`, a schema per verb), read
  by the docs checker. Exit codes: 0 success, 1 failure, 2 usage, **3 not signed in**. Windows-only verbs (issue, discover, templates) check the
  operating system and say plainly what to run where.
- *Finding the console and the server.* A file `dbdatakit.yml`, found by walking up from the current directory (the git and `global.json`
  convention) or named by `--config`; a user-level file (`%APPDATA%\DbDataKit\dbdatakit.yml`, `~/.config/dbdatakit/dbdatakit.yml`) is merged
  underneath, the nearer file winning per key. It holds no secrets, so it can be committed: the consoles by name with their address and the
  fingerprints accepted for the console's own certificate, and per server the DNS names, CA and template its certificate is requested with. The console
  is chosen by `--console <name-or-url>`, then the variable servers already use to say where the console is, then `default:` in the file, then the only
  entry; otherwise an error lists the names. A server is named by `--server`, a name the console lists; a command that needs one and gets none lists
  the choices.
- *Trust on first use for the console's certificate.* When the system does not trust it, the command shows its subject, issuer, dates and fingerprint
  and asks, like an SSH host key; a script passes `--trust <fingerprint>`. It is saved in the user-level file only when the person says so. There is no
  "skip verification" flag.
- *Sign-in by device code*, in the shared sign-in controllers so any host that has them offers it. `POST /api/auth/cli/start` (anonymous, rate limited)
  returns a 256-bit device code, an 8-character user code, the approval link, an expiry of **10 minutes** and a polling interval, and records the
  caller's address and a label such as `dbdatakit on WS-ADMIN01`. The approval page needs a signed-in person and shows the code, the label, the
  requesting address and the console's own certificate fingerprint (to compare with the one the command prints), with Approve and Deny; wrong codes are
  rate limited and say nothing about other codes. `POST /api/auth/cli/poll` (anonymous, rate limited) answers pending, denied, expired, or approved
  with a session, once. The session is an ordinary session of the **approving person**, with their roles, labelled `cli` in the audit, for the shared
  lifetime (12 hours, not sliding), sent as the session cookie so every existing policy applies. Methods: `browser` (the default: a link and a code,
  the browser opened when it can), `code` (never opens a browser; chosen over ssh or without a display), `local` (terminal only: someone on the
  console's host approves the code with a console command that writes to the state database the way `user invite` does; in the first version) and
  `windows` (negotiates with the workstation's signed-in identity against the existing Windows sign-in). The session is cached per console,
  protected (DPAPI for the current user on Windows, a 0600 file elsewhere). A `401` becomes "not signed in to <console>; run login" with exit code 3;
  nothing signs in again silently.
- *The flow.* Sign in; discover the CA and template (LDAP with Kerberos); an ephemeral RSA key (3072 bits unless the template asks for more) and a CSR;
  submit over RPC/DCOM; on *issued*, a PKCS#12 in memory with the leaf, the chain the CA returned and the key, encrypted with a one-time passphrase;
  stage it on the endpoint through the relay, activate it, check what is served (CERTIFICATES-4), delete the key and the pending record. A request
  that needs manager approval is *pending*: its key is kept on the workstation, protected with DPAPI for the current user, until it is retrieved,
  denied or swept after **14 days**. An `ICaSubmitter` seam keeps the logic testable on Linux CI.
- *Commands of a first version.* `login`, `logout`, `whoami`; `show` (what the configuration resolved to, from which files); `server list`,
  `server status`; `cert status` (served certificate, days left, a staged one waiting), `cert discover`, `cert templates`, `cert issue`,
  `cert push --pfx` (the same from a PFX already in hand), `cert retrieve --request-id`, `cert sync` (every endpoint in the file inside its renewal
  window); and `check`.
- *`check`, the diagnostic ladder.* It walks a ladder and stops at the first failure with its cause and, when only the host can repair it, the exact
  command to run on that host: (1) configuration: which file, which console, which server; (2) reaching the console: DNS, TCP, then its TLS state
  (trusted, untrusted with its fingerprint, expired, wrong name, plain HTTP on a TLS port); (3) sign-in: which methods the console offers, whether a
  session is cached and valid, which roles; (4) each server: reachable through the relay, its trust entry matches what it presents (if not, "its
  certificate is not on the trust list; pair again or add the new fingerprint"), health, days left, a staged certificate waiting. It cannot repair a
  server that does not answer; it prints the host's command instead. The verb is `check`, as for services (`service check`).
- *An expired console certificate.* The command shows the fingerprint and asks, or takes `--trust <fingerprint>`; the approval page in a browser shows
  a certificate warning the person clicks through after comparing fingerprints, or the `local` method avoids the browser. The console's own certificate
  is renewed on the console's host.

**Open questions.**

- Its name: `dbdatakit` is the console's own command line (`docs/architecture.md`), so this is either a mode of `dbdatakit` or a separate tool with
  another name.
- Sign-in: the device-code approval above, or a personal access token.
- The key passes through the console in memory on its way to the service. The console is already trusted with every relayed call and nothing logs
  bodies, but it is new exposure; a direct path from the command line to the service would avoid it, at the cost of a second credential model.
- Device-code consent phishing: someone could get a person to approve a code. The page shows the label and the requesting address, codes last 10
  minutes, and the session has only the approver's own roles.
- Unattended renewal: a scheduled `cert sync` cannot approve a sign-in, and sessions last 12 hours, so a person runs it; a service identity for it is
  a later design.
- A pending request keeps an encrypted key on the workstation for up to 14 days.
- Real CA behaviour is unproven since the enroller moved into the shared package (CERTIFICATES-2), and a wrong template is silent at the CA (the
  importer's name check catches it).

### A Linux enroller

A Linux host enrolls and renews its own certificate, through CES (certificate enrollment web services) or a `certmonger` wrapper, behind the same enroller
interface as CERTIFICATES-1, with CA and template discovery over LDAP with Kerberos. It waits for the CA team to publish Kerberos-authenticated CES on the
new CA (it publishes none today, and the legacy CA's CES answers 503) and to grant the Linux machines' computer accounts Enroll on the template.

### Trusting a company CA as a kind of trust-list entry

**What.** Besides fingerprints, a trust list could hold an entry "any certificate the company CA issues for this name", the way the console's `HomeTrust`
already trusts the identity home. **Why.** A certificate the CA renews every year would need no rollover: when AD CS renews a Gateway's certificate, a CA
entry means the console never notices, while with fingerprints it must learn each new one. **Open questions:** where it applies, since management
certificates stay self-signed (the command line's trust of the console, a link to a user-facing endpoint); how the name and the chain are verified when the
enterprise root may not be in the server's store; whether a CA entry and fingerprint entries sit in one list.

## Shared areas touched

- `DbDataKit.Hosting.Certificates`, `DbDataKit.Hosting.Certificates.Windows`, `DbDataKit.Hosting.Cli` (`CertCommand`), the console's relay and server
  screens, the pairing records of `DbDataKit.Web` (shared with the join), the console's and the Gateway's management APIs.
