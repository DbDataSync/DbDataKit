# APPHOSTING: several sites with their own host names

## Goal

One Gateway serves several sites (apps, portals, views, store) each under its own host name on the public listener, with its own certificate, verified
ownership of the name, the right sign-in redirects and cookies, behind listed proxies. App code runs in child processes. Sign-in is proven on real hosts.

## What exists today

- App address bindings on the apps listener (`AppAddress`, `AppAddresses`: name, kind, path, certificate secrets, primary; `app-instance` format 2),
  DNS-TXT verification, SNI certificates and redirect URIs for those bindings (`docs/apps.md`).
- Not built: the home's side of redirect URIs when the host client is held by another identity home, certificate renewal of a bound name, bindings for
  anything but the apps module, forwarded headers from listed proxies, cookie scope per site.
- App code runs in the apps module's process; the semantic calls are not in process as the signed-in user.
- Windows sign-in on a domain-joined host and Entra sign-in on a tenant run with fakes in CI only (`Shared/src/DbDataKit.Identity/README.md`,
  "Not yet run on a real host"); Windows sign-in on Linux with a keytab is untested.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| APPHOSTING-1 | The host binding table on the public listener: a host name maps to a site (an app instance, a portal, the views or the store under a path) | none | M | two host names on one listener reach two portals; an unbound name answers 404 |
| APPHOSTING-2 | SNI certificates and DNS-TXT verification for every bound host name (generalised from app bindings) | APPHOSTING-1 | M | a TLS client sees each name's certificate; an unverified name is refused at bind time |
| APPHOSTING-3 | Redirect URIs per site, including the home's side when the host client is held by another identity home | APPHOSTING-1 | S | sign-in on two sites returns to each; removing a binding removes its redirect URI at the home |
| APPHOSTING-4 | Forwarded headers accepted only from listed proxies | none | S | a request through a listed proxy keeps the client address and scheme; the same headers from another address are ignored |
| APPHOSTING-5 | Certificate renewal for bound host names (`RenewBeforeDays`), with an alert before expiry | APPHOSTING-2 | S | a fake clock past the window renews and the listener serves the new certificate without a restart |
| APPHOSTING-6 | Cookie scope per site and the sign-in issuer per site or shared, as APPHOSTING-P1 says | APPHOSTING-P1 | M | sessions of site A are not sent to site B; the issuer seen by each site is the one APPHOSTING-P1 chose |
| APPHOSTING-7 | App code in child processes, and the apps module's calls to `semantic` in process as the signed-in user | none | L | a crash of one app's process does not stop another app; a semantic call from an app runs as the person |
| APPHOSTING-8 | Sign-in on real hosts: Windows sign-in on a domain-joined host, Entra on a tenant, Windows on Linux with a keytab; Entra group overage handled as `Shared/src/DbDataKit.Identity/README.md` says; `dbdatakit identity run` offers the upstream sign-ins (Entra, Windows) as the Gateway's identity module does; a group of identity homes protects its signing keys with the group's key instead of a data-protection folder copied between them | none | M | checked by hand (no domain, KDC or tenant in CI): the steps and results in the Identity README; in CI: `identity run` signs a person in through a stand-in Entra tenant, and two homes of one group read each other's signing keys with no folder copied |

## Needs from other subjects

- The public listener exists (`docs/serving-layer.md`, "The public listener"): a module mounts on it by a path prefix, or by a claim on the request, as the apps host claims the names bound to an instance. Binding a host name to a portal, the views or the store is APPHOSTING-1.

## Proposed decisions

### APPHOSTING-P1: one sign-in issuer for every site, or one per site (awaiting the owner)

Option A: one issuer (`https://login.example/identity/`) shared by every site; each site is a client of it. Option B: each site has an issuer under its
own host name. Scenario: a person signs in on `sales.example` and then opens `hr.example`. Under A they are signed in already (one session at the issuer);
under B they sign in again, and a cookie of one site never reaches the other. Recommendation: A, with cookies scoped per site.

## Shared areas touched

- `DbDataKit.Apps.*`, `DbDataKit.Identity`, `DbDataKit.Hosting.Certificates`, `DbDataGateway.Server` hosting.
- `docs/apps.md`, `docs/formats.json` (`app-instance` bindings if widened), `docs/ui-editors.md`.
