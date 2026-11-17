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
- The console remembers one fingerprint per paired server's management certificate (a pin); INSTALLER-11 makes the pin go both ways. Renewing a
  self-signed management certificate changes its fingerprint, so today the link breaks until the server is paired again.
- An AD CS enroller exists as a library and nothing exposes it: `DbDataKit.Hosting.Certificates.Windows` has CA discovery (`CaDiscovery`), the template
  catalogue, the request (`CertificateBuilder`, `AdcsEnrollment`, `AdcsCertificateEnroller`), pending requests (`PendingEnrollmentStore`,
  `EnrollmentCompletion`) and the store. Its commands lived in the deleted Sync server; its messages still name `dbdatasync config cert retrieve`, which no
  longer exists. It has not run against a real CA since it moved into the shared package.
- The design here comes from a 2026-11-02 proposal on an old branch (in git under the old plans folder's `needs-review/`); its rename of the console and its
  `doctor` command are overtaken.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CERTIFICATES-1 | The AD CS enroller behind the shared `cert` command of the console and the Gateway, run on a domain-joined Windows host for its own user-facing endpoint: `cert discover`, `cert templates`, `cert enroll --template <name> [--name <dns name> ...]`, `cert retrieve` (a pending request); an issued certificate goes through CERTIFICATES-3 and CERTIFICATES-4. The enroller's messages name these commands. An `ICaSubmitter` seam so the logic is tested without a CA | none | M | tests with a fake CA on Linux CI: issued, pending then retrieved, denied, failed; nothing is left behind after each outcome; a unit test reads the message of a pending request and finds a command that exists; the Windows job covers the DPAPI-protected pending key |
| CERTIFICATES-2 | A check against both real enterprise CAs, from a Windows host with an administrator's own account: discover, templates, enroll and retrieve for a test name; the template settings that work (serverAuth, "supply in the request" for subject and names, Enroll permission, manager approval or not) are written down | CERTIFICATES-1 | S | by hand, because no CA is in CI: the checklist and its result are in `Shared/src/DbDataKit.Hosting.Certificates.Windows/README.md`; anything the move broke is fixed with a unit test |
| CERTIFICATES-3 | `CertificateImporter` in `DbDataKit.Hosting.Certificates`: validate a PFX before anything is written (the key matches, valid now, serverAuth, its names cover the endpoint's configured names; a chain that does not build is a warning), replace atomically keeping the previous one, re-protect with a fresh random password; at start, a current certificate that will not load falls back to the previous one with a loud log line and a degraded status, never plain HTTP | none | M | tests on Linux and Windows: valid, expired, wrong key, wrong names, wrong EKU, corrupt PFX, wrong passphrase; a failed import leaves the served certificate; no key or password in plain text on disk; the fallback at start is logged and reported |
| CERTIFICATES-4 | Replacing a user-facing certificate: stage (stored, not served), activate (swapped in process where the listener allows, else at the next restart, which is reported), check what is served, keep the previous one; on the console and the Gateway, from `cert` and from the console's server screen through the relay (administrator only) | CERTIFICATES-3, INSTALLER-2 | M | tests on the console and the Gateway: staging changes nothing served; activating swaps and reports the fingerprint; a bad PFX leaves the served certificate; a sentinel test finds no key or passphrase in any response, log line or audit entry |
| CERTIFICATES-5 | Trust lists for management certificates: the console's pin of each server and each server's pin of the console (INSTALLER-11) become a list of fingerprints, each with the date it was added and its expiry; every link accepts any listed certificate; an entry stays until its certificate expires or an administrator removes it, and an expired entry is removed automatically; the console's server screen and `dbdatakit server trust list\|remove` show and change it | INSTALLER-11 | M | tests: both of two listed certificates are accepted and an unlisted one refused on every path (relay over HTTP and WebSocket, status, key hand-over); a swap between two listed certificates never drops the link; an expired entry is removed on a fake clock; a removed entry is refused at once |
| CERTIFICATES-6 | Automatic renewal of self-signed management certificates, during the day, about a week before expiry: the server makes the new certificate and stages it, reports its fingerprint over the link the old one authenticates, the console adds it to its list (and the server adds the console's new one to its own list when the console renews), the server activates it, the console confirms the new one is presented; both certificates stay listed on both sides until the old one expires; the administrator is notified that it happened and how to check it | CERTIFICATES-5, ALERTING-4 | M | end to end on a fake clock between the console and a Gateway, in both directions: renewed inside the window and only in daytime hours, the link never drops, both entries listed until the old one expires, then the old one removed, and a notification raised; a renewal whose activation fails keeps serving the old certificate and notifies |
| CERTIFICATES-7 | Renewal of an enterprise certificate on a user-facing endpoint: during the day, about a week before expiry, a host that can enroll itself (CERTIFICATES-1) requests the new certificate and replaces it (CERTIFICATES-4), and notifies the administrator to verify it; a host that cannot enroll notifies the administrator well before expiry instead; days left show in `cert status`, `service check` and the console | CERTIFICATES-1, CERTIFICATES-4, ALERTING-4 | M | a fake clock inside the window renews once and only in daytime hours (a second run does nothing); outside it nothing happens; the notification is raised in both cases |

## Needs from other subjects

- INSTALLER-2: the startup sequence's certificate step, which CERTIFICATES-3's fallback and CERTIFICATES-4's replacement plug into.
- INSTALLER-11: the pins both ways, which CERTIFICATES-5 turns into lists.
- ALERTING-4: the notifications of a renewal and of a certificate near expiry.
- APPHOSTING-5: renewal of bound host names' certificates uses CERTIFICATES-3's importer and CERTIFICATES-7's window rather than a second mechanism.

## Open designs

Not scheduled. Each becomes tasks only when the owner schedules it.

### A command line that runs no server, for certificates

A person on a domain-joined Windows workstation acts as coordinator and certificate publisher for a service on Linux, which cannot reach the enterprise CA
(the CA takes requests over RPC/DCOM from domain machines). The command line signs in to the console as that person, makes the key in memory, requests
the certificate from the CA, and sends the PFX to the service through the console (CERTIFICATES-4's replacement), so no private key is copied by hand.
Open questions: how it signs in (a personal access token or a device-code approval in the browser); that the key passes through the console in memory on
its way; whether it is a separate tool or a mode of `dbdatakit`; how a pending request (manager approval at the CA) keeps its key on the workstation
until it is retrieved (the old design: DPAPI for the current user, swept after 14 days).

### A Linux enroller

A Linux host enrolls and renews its own certificate, through CES (certificate enrollment web services) or a `certmonger` wrapper, behind the same enroller
interface as CERTIFICATES-1, with CA and template discovery over LDAP with Kerberos. It waits for the CA team to publish Kerberos-authenticated CES and to
grant the Linux machines' accounts Enroll on the template.

## Shared areas touched

- `DbDataKit.Hosting.Certificates`, `DbDataKit.Hosting.Certificates.Windows`, `DbDataKit.Hosting.Cli` (`CertCommand`), the console's relay and server
  screens, the pairing records of `DbDataKit.Web` (shared with INSTALLER-11), the console's and the Gateway's management APIs.
