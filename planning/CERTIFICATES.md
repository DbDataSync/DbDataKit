# CERTIFICATES: enterprise certificates, renewal and rollover

**Needs the owner's review before it is accepted:** this subject restores the certificate work of a 2026-11-02 proposal (see "What exists today"); nothing here is agreed yet.

## Goal

Every server (the console, Workspace, Gateway and Runner, on Windows or Linux) can serve a certificate from the company's Active Directory Certificate
Services, get it without anyone copying a private key by hand, renew it before it expires, and replace it without breaking its link to the console. A
server's certificate is checked before it is served, the previous one is kept, and a certificate that will not load never turns a server into plain HTTP.

## What exists today

- Each server makes and renews its own managed self-signed certificate (`DbDataKit.Hosting.Certificates`: `SelfSigned`, `CertificateProvider`,
  `ProtectedPasswordFile`); the shared `cert` command has `status` and `renew` for it (`Shared/src/DbDataKit.Hosting.Cli/CertCommand.cs`).
- An AD CS enroller exists as a library and nothing exposes it: `DbDataKit.Hosting.Certificates.Windows` has CA discovery (`CaDiscovery`), the
  template catalogue, the request (`CertificateBuilder`, `AdcsEnrollment`, `AdcsCertificateEnroller`), pending requests (`PendingEnrollmentStore`,
  `EnrollmentCompletion`) and the store. Its commands lived in the deleted Sync server; its messages still tell people to run
  `dbdatasync config cert retrieve`, which no longer exists. It has never run against a real CA since it moved into the shared package.
- The console remembers one fingerprint per paired server (a pin). Replacing a server's certificate breaks the link until it is paired again.
  INSTALLER-11 makes the pin go both ways (the server also pins the console).
- A 2026-11-02 proposal (on an old branch: DbDataKit Studio, trust lists and a separate admin tool) designed most of this; its rename and its separate
  tool are overtaken (the console keeps its name, and `dbdatakit` is the console's own command line). The certificate design below is taken from it.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CERTIFICATES-1 | The AD CS enroller behind the shared `cert` command on every server's command line, run on a Windows host for its own server: `cert discover`, `cert templates`, `cert enroll --template <name> [--name <dns name> ...]`, `cert retrieve` (a pending request); an issued certificate goes through CERTIFICATES-3 and is served at the next start. The enroller's messages name these commands. An `ICaSubmitter` seam so the logic is tested without a CA | none | M | tests with a fake CA on Linux CI: issued, pending then retrieved, denied, failed; nothing is left behind after each outcome; a unit test reads the message of a pending request and finds a command that exists; the Windows job covers the DPAPI-protected pending key |
| CERTIFICATES-2 | A check against both real enterprise CAs, from a Windows host with an administrator's own account: discover, templates, enroll and retrieve for a test name; the template settings that work (serverAuth, "supply in the request" for subject and names, Enroll permission, manager approval or not) are written down | CERTIFICATES-1 | S | by hand, because no CA is in CI: the checklist and its result are in `Shared/src/DbDataKit.Hosting.Certificates.Windows/README.md`; anything the move broke is fixed with a unit test |
| CERTIFICATES-3 | `CertificateImporter` in `DbDataKit.Hosting.Certificates`: validate a PFX before anything is written (the key matches, valid now, serverAuth, its names cover the server's configured names; a chain that does not build is a warning), replace atomically keeping the previous one, re-protect with a fresh random password; at start, a current certificate that will not load falls back to the previous one with a loud log line and a degraded status, never plain HTTP | none | M | tests on Linux and Windows: valid, expired, wrong key, wrong names, wrong EKU, corrupt PFX, wrong passphrase; a failed import leaves the served certificate; no key or password in plain text on disk; the fallback at start is logged and reported |
| CERTIFICATES-4 | One certificate contract on every server's management API (administrator only), in a shared library composed into the four servers: `GET /api/v1/certificate` (served certificate, names, fingerprint, days left, source, a staged one, restart needed), `POST /api/v1/certificate/stage` (a PFX and its passphrase; stored, not served; answers the fingerprint), `POST /api/v1/certificate/activate` (serves the staged one, swapping in process where the listener allows, else reporting "restart needed"), `DELETE /api/v1/certificate/stage`; the console relays them | CERTIFICATES-3, INSTALLER-2 | M | API tests on each server through the console's token and the local channel: staging changes nothing served; activating swaps and reports the fingerprint; a bad PFX leaves the served certificate; a sentinel test finds no key or passphrase in any response, log line or audit entry |
| CERTIFICATES-5 | Trust lists: the console's pin of a server, and the server's pin of the console (INSTALLER-11), become a list of fingerprints, each with the date it was added; every link accepts any listed certificate; the console's server screen and `dbdatakit server trust list\|add\|remove\|verify` show and change it; a list with two entries for more than a week is a warning | INSTALLER-11 | M | tests: both of two listed certificates are accepted and an unlisted one refused on every path (relay over HTTP and WebSocket, status, key hand-over); a swap between two listed certificates never drops the link; the screen shows which entry was seen last |
| CERTIFICATES-6 | Rollover without a gap, from the console's server screen and `dbdatakit server cert rollover <server>`: stage on the server (its answer, over the link the old entry authenticates, vouches for the new fingerprint), add it to the trust list, activate, connect and confirm the new one is served, remove the old entry (default; `--keep-old` keeps it); an activation that falls back keeps both entries and says so | CERTIFICATES-4, CERTIFICATES-5 | M | end to end against the console and a test Gateway: the five steps in order; a failed activation exits non-zero with both entries kept; an expired but listed certificate is replaced |
| CERTIFICATES-7 | A certificate for a server that cannot enroll itself (a Linux server, or a Windows host outside the domain), requested where CERTIFICATES-P1 says and sent to the server through CERTIFICATES-6 | CERTIFICATES-1, CERTIFICATES-6, CERTIFICATES-P1 | M | end to end with a fake CA: the request, the stage, the rollover; the key exists only in memory and on the target server; a pending request survives until retrieved and is swept after 14 days |
| CERTIFICATES-8 | Renewal: a server whose certificate came from AD CS renews it inside the shared `RenewBeforeDays` window (as CERTIFICATES-P2 says who runs it) and rolls it over; days left show in `cert status`, `service check` and the console; near expiry raises an alert through ALERTING-4 | CERTIFICATES-6, CERTIFICATES-P2 | M | a fake clock inside the window renews once (a second run does nothing); outside it nothing happens; the alert is raised on the fake clock |
| CERTIFICATES-9 | CA and template discovery that works from Linux (LDAP with Kerberos) | CERTIFICATES-1 | S | parser tests on captured directory answers; one live check by hand |
| CERTIFICATES-10 | A Linux enroller (CES, or a `certmonger` wrapper) behind the same enroller interface, so a Linux server enrolls and renews itself | CERTIFICATES-9 | M | deferred until the CA team publishes Kerberos-authenticated CES and grants the Linux machines' accounts Enroll; then a live check by hand and fake-endpoint tests in CI |

## Needs from other subjects

- INSTALLER-2: the startup sequence's certificate step, which CERTIFICATES-3's fallback and CERTIFICATES-4's contract plug into.
- INSTALLER-11: the pins both ways, which CERTIFICATES-5 turns into lists.
- ALERTING-4: the console's whole-suite health check raises the near-expiry alert CERTIFICATES-8 feeds.
- APPHOSTING-5: renewal of bound host names' certificates uses CERTIFICATES-3's importer and CERTIFICATES-8's window rather than a second mechanism.

## Proposed decisions

### CERTIFICATES-P1: where a certificate for a server that cannot enroll itself is requested (awaiting the owner)

Scenario: a Gateway runs on Linux. The enterprise CA accepts requests only over RPC/DCOM from domain machines, so the Linux host cannot ask it.

- **A. From an administrator's Windows workstation.** The administrator runs `dbdatakit server cert issue <server>` there, signed in to the console
  with their personal access token. The key is made in memory on the workstation, the CA issues the certificate, and the PFX goes to the server through
  the console's relay (CERTIFICATES-6). The key passes through the console in memory; nothing logs it.
- **B. From the console's host,** when it is a domain-joined Windows machine, on the administrator's request from the console's screen. Same flow,
  but the console's host account needs Enroll on the template, and a console on Linux cannot do it.
- **C. Wait for CERTIFICATES-10,** so the Linux server enrolls itself; until then it keeps its self-signed certificate.

Recommendation: A now (it works whatever the console runs on), C when the CA team publishes CES. Windows servers in the domain enroll themselves
(CERTIFICATES-1) either way.

### CERTIFICATES-P2: who renews an enterprise certificate when nobody is at a keyboard (awaiting the owner)

Scenario: the Gateway's certificate expires in 25 days, inside the 30-day window, at night.

- **A. The server renews itself** where it can enroll (a domain-joined Windows host, later a Linux host with CES), as in-process housekeeping with its
  machine account, then rolls over. A server that cannot (P1's case) raises the near-expiry alert and a person renews it.
- **B. Always a person,** prompted by the alert, for every server.

Recommendation: A.

### CERTIFICATES-P3: trust a list of fingerprints, or trust the enterprise CA (awaiting the owner)

Scenario: certificates are renewed every year. With trust lists each renewal is a rollover (CERTIFICATES-6). With trust in the enterprise CA, the console
would accept any certificate that CA issued for the server's name, so a renewal needs no rollover, but anything the CA issues for that name is accepted.

Recommendation: trust lists first (this subject); trusting a CA as an extra kind of list entry later, if rollovers become a burden.

## Shared areas touched

- `DbDataKit.Hosting.Certificates`, `DbDataKit.Hosting.Certificates.Windows`, `DbDataKit.Hosting.Cli` (`CertCommand`), the console's relay and server
  screens, the pairing records of `DbDataKit.Web` (shared with INSTALLER-11), each server's management API.
