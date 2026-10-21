# Shared TLS / certificate / service rollout

Integration branch: `shared-tls-rollout`. One branch and PR per slice, named `shared-tls-rollout-NN-<name>`.
Each slice is only done when its test column passes.

| # | Slice | Shared change | Adopted by | Test |
|---|-------|---------------|------------|------|
| 0 | Wire Sync to the shared libs | None. Copy Proxy's `DbDataKitReference` mechanism into Sync's build files. | Sync (builds only) | Sync CI green, with no behaviour change |
| 1 | Expiry evaluator | Compare Sync's `CertificateExpiryEvaluator` (49 lines) with the shared `CertificateExpiry`, pick one rule, extend the shared one if needed. | Sync swaps in and deletes its copy | Existing Sync evaluator tests run unchanged against the shared one |
| 2 | Protected PFX password and legacy migration | Move `ProtectedPasswordFile` and `MigrateUnprotectedPfx` out of Proxy into `Hosting.Certificates`, with tests. | Proxy (delete its copies) | Proxy's `TlsKeyProtectionTests` pass unchanged |
| 3 | Sync's self-signed path | None, or small additions. | Sync's new-self-signed and its daily renewal service use the shared generator and provider, including the protected password and ACL. Its generation code is retired. | `SelfSignedCertificateServiceTests` and `NewSelfSignedFileTests` pass; a migration test for an existing unencrypted `tls/dbdatasync.pfx` |
| 4 | Windows store and private-key ACL | New package `DbDataKit.Hosting.Certificates.Windows`, taking Sync's `CertificateStore`, `PrivateKeyAccess`, `InstalledServiceAccount` and `CertificateSanReader`. Implements the shared Store mode. | Sync (its Windows tests move with the code) | Sync's Windows tests and `AdminCertificateServiceWindowsTests` |
| 5 | ADCS enrollment | Move into the same Windows package: `AdcsEnrollment`, `CaDiscovery`, `CertificateTemplateCatalog`, `EnrollmentCompletion`, `PendingEnrollmentStore`, `CertificateKeys`. Implements Enroll. | Sync | Existing ADCS unit tests; a Windows CI job for the rest |
| 6 | Proxy gains Store mode | None. | Proxy adds `Proxy:Tls:Mode=Store` with thumbprint/subject. First user of the shared Store code beyond Sync. | New `TlsTests` cases; a store test on windows-latest |
| 7 | Shared cert command | Add an extension point so a product can register extra verbs. Sync's file/store/enroll verbs become registrations on it. | Sync keeps all its current `config cert ...` verbs (a compatibility test lists them) | CLI snapshot tests of help output and exit codes |
| 8 | Service command | Likely none. | Sync's Windows service install/uninstall/status move onto the shared provisioner, and Sync gains start, stop, restart and doctor. The Sync MSI can then call `dbdatasync service start` instead of PowerShell. | Characterization tests of today's install behaviour, then an e2e service install on Windows CI (MSI harness exists) |
| 9 | Systemd and Docker generation | None. | Sync's `SystemdService` moves onto `Hosting.Systemd` | Unit-file snapshot test |
| 10 | PlainHttpGuard in Sync | None. | Sync refuses plain HTTP on a non-loopback bind unless explicitly allowed | A guard test; needs a decision on how it interacts with running behind a TLS proxy |

## Decisions

- Managed PFX: Sync moves to Proxy's approach (encrypted, random password in a protected file). No migration of the
  old unencrypted `tls/dbdatasync.pfx`; this is a documented breaking change.
- Renewal rule: the shared `RenewBeforeDays` (default 30). Sync's one-third-of-lifetime rule goes away.
- Expiry check: one rule, `CertificateExpiry.Evaluate` (`ExpiringSoon` when floored days remaining < threshold).
  Sync's service keeps its own once-per-UTC-day notification throttle inline; the shared library has no notification concept.
- Sync's file layout is unchanged: the data directory is the deployed instance's repo root, tracked with git there.
- PlainHttpGuard in Sync: honour forwarding headers (X-Forwarded-Proto) when behind a TLS-terminating proxy.
- CI: slice PRs target `shared-tls-rollout`, which triggers no product CI. Test each slice locally; run the full CI
  on the group PR into `main`, plus a manual `workflow_dispatch` run at checkpoints.

## Status

- 0: merged (#45).
- 1: merged (#46, moved the evaluator) then redone (this PR): Sync's evaluator is deleted and its service uses the shared `CertificateExpiry`.
- 2: shared `ProtectedPasswordFile` + `MigrateUnprotectedPfx` in `Hosting.Certificates`; Proxy keeps a 4-line subclass that pins its existing on-disk format identifiers so installed password files still load.
- 3: Sync's managed self-signed file uses the shared generator, `CertificateProvider` and `ProtectedPasswordFile`
  (encrypted PFX, `tls/dbdatasync.pfx.pwd`, ACL-restricted, 30-day renewal). The host passes the password to Kestrel
  in memory. Breaking changes, documented in `DbDataSync/docs/certificates-self-signed.md`: an older unencrypted file
  is replaced rather than migrated; the certificate subject is the machine name (the console host is a SAN); renewal
  is 30 days before expiry instead of a third of the lifetime; a file that cannot be opened is replaced instead of
  failing the renewal check.
- 4: new package `DbDataKit.Hosting.Certificates.Windows` holds `CertificateStore` (install/list/find), `PrivateKeyAccess` and
  `InstalledServiceAccount`. Differences from the plan: (a) `CertificateSanReader` is deleted, not moved: it is plain ASN.1, not
  Windows-specific, and the shared library gains `SelfSignedCertificateGenerator.ReadDnsNames`; (b) `InstallSelfSigned` stays in Sync
  (`StoreSelfSignedCertificate`) because it needs `CertificateKeys`/`CertificateBuilder`/`CertificateSpec`, which move in slice 5
  or stay with Sync; (c) the Sync Windows tests that build certificates with Sync's `CertificateBuilder`/`CertificateKeys`
  (`CertificateStoreWindowsTests`, `InstalledKeyWindowsTests`) stay in Sync for the same reason; only `InstalledServiceAccountTests` moved;
  (d) the shared provider's `Store` mode already existed (it reads `X509Store` directly, valid certificates only), so nothing was
  rewired; Sync's lookups (find by subject/thumbprint, including expired ones) are the package's `CertificateStore`.
- 5: AD CS enrollment moved into `DbDataKit.Hosting.Certificates.Windows`: `AdcsEnrollment`, `CaDiscovery`, `CertificateTemplateCatalog`,
  `EnrollmentCompletion`, `PendingEnrollmentStore`, `CertificateKeys`, plus what they need (`CertificateSpec`, `CertificateBuilder`) and
  slice 4's leftover `StoreSelfSignedCertificate`. New `AdcsCertificateEnroller : ICertificateEnroller` implements the shared Enroll mode
  (throws `EnrollmentPendingException` when the CA queues the request). Every Sync test that exercises these types moved with them
  (41 tests; 26 run on Linux). The enroller, like `AdcsEnrollment`, has never been run against a real CA from CI or this sandbox.
  `DbDataSync.Certificates` now holds only Sync's own pieces: `CertificateBinding`, `CertificateInfo`, `ManagedSelfSignedCertificate`.
  Leftover: the completion handler's recovery text still names `dbdatasync config cert retrieve`.
- 6: Proxy gains `Proxy:Tls:Mode=Store` (`CertificateThumbprint` or `CertificateSubject`, `StoreLocation` LocalMachine by default),
  through the shared provider's Store mode. The rotation service also picks up a renewed store certificate (same subject, later
  expiry) without a restart; `cert show|check` describe it; `cert ensure|renew` explain that the store owns renewal. Config validation,
  README and the example config updated. The new store tests use the `CurrentUser` store and pass on Linux as well as Windows.
- 7: the shared `CertCommand` gets a verb table (`CertVerbs`; `cmd.Verbs.Add(name, usage, handler)`): registered verbs run before the
  built-ins (so a product can replace one by name) and their usage is appended to the usage text. Sync's `config cert` dispatch is now a
  `CertVerbs` registration list (`use-pem`, `use-pfx`, `status`, `new-self-signed`, `list`, `enroll`, `renew`, `retrieve`, `templates`,
  `discover`, `bind`); its usage text and exit codes are unchanged and pinned by `CertCommandSnapshotTests`, which also lists the verbs.
  Sync uses the table directly rather than instantiating the shared `CertCommand`, because the shared built-ins (`show`, `ensure`, `check`)
  need a `CertificateOptions` that Sync only has once a repo root is resolved, and adding verbs to Sync's CLI was not part of this slice.
- 9: Sync's `SystemdService.RenderUnit` is generated by the shared `SystemdUnitGenerator`. The generator gained a `Hardening` flags
  option (default `All`, so Proxy's unit is byte-identical) and accepts a quoted absolute `ExecStart`. Sync asks for exactly the
  directives it always had (managed root: `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome`; other roots: only `ReadWritePaths`),
  not the generator's full sandbox (`PrivateTmp`, `UMask=0077`, dropped capabilities, ...), because those would change what files the
  service writes and who can read them on existing installs. Decision for the owner: whether to move Sync to the full sandbox (a
  one-line change, `Hardening = All`, but it needs testing on a real Linux install: the config repo's files become 0600/0700, a
  Kerberos ticket cache in `/tmp` becomes invisible to the service). The unit text changes only in form: `Environment="DOTNET_ROOT=..."`
  is quoted, sections are labelled, and `TimeoutStopSec=90` (systemd's own default) is stated.
- 10: Sync refuses plain HTTP from a non-local client unless it arrived over HTTPS or through a trusted TLS-terminating proxy
  (`PlainHttpGuard.RequestAllowed` in the shared library; `PlainHttpProtection` middleware in Sync, first in the pipeline). Proxies are
  listed in `DbDataSync:App:TrustedProxies` (IPs or CIDR ranges): only their `X-Forwarded-For`/`X-Forwarded-Proto` are believed.
  `DbDataSync:App:AllowInsecureHttp` turns the check off (not editable from the Admin screen). Loopback clients and `/api/health` are
  always served. Startup logs a warning when a plain non-loopback address is configured with neither setting. Breaking: the container
  image listens on `http://0.0.0.0:8080`, so a browser reaching it through Docker's port mapping is refused until
  `AllowInsecureHttp` (the sample compose file sets it, for evaluation) or `TrustedProxies` is set; documented in `docs/install.md`
  and `docs/configuration.md`.
- 8: not done. See the pull request thread / hand-off notes: it rewrites Windows service installation, which cannot be exercised
  without Windows, and the plan's own test for it is an end-to-end install on Windows CI.
- 11: the Docker images now default `DbDataSync__App__AllowInsecureHttp=true` (decided by the owner after slice 10), so `docker run -p 8080:8080` works from a browser;
  a deployment behind a proxy sets it back to `false` and lists the proxy in `TrustedProxies`. The sample compose file no longer sets it.
- 8: Sync's Windows `service install|uninstall|status` run on the shared `ServiceProvisioner` (Service Control Manager P/Invoke instead of
  `sc.exe`), and Sync gains `start`, `stop`, `restart` (each waits for the state) and `doctor` (the shared `ServiceDoctor` plus a data-directory
  check). Kept as before: `LocalSystem` by default (no `--allow-localsystem` needed), start mode automatic (not delayed), no failure-recovery
  actions, no service SID, Sync's own elevation messages and exit code 1, the `icacls` ownership/grant step, the Event Log source, the
  launcher conversion and the registration record. Changes: `install` is idempotent (updates an existing registration instead of failing
  with 1073), `status` prints the shared format instead of `sc query`'s, `--password-stdin` allows a named user account, the command line is
  quoted by the shared rules (an unquoted `--repo` when the path has no spaces; equivalent to the SCM). Tests: the whole flow runs on any OS
  against `InMemoryServiceControlManager` (`ServiceCommandInstallTests`), and `DbDataSync/tools/service-e2e.ps1`, run by the Windows job in
  `dbdatasync-ci.yml`, drives the real manager end to end.

## Follow-up considerations (decisions deliberately left open)

1. **Sync's systemd hardening (slice 9).** The unit keeps exactly the directives it has always had. The shared generator's full sandbox is one
   line away (`Hardening = SystemdHardening.All` in `SystemdService.RenderUnit`) but changes behaviour on existing installs: `UMask=0077` makes
   the files the service writes in the config repository readable only by the service user (an operator running `dbdatasync config check`
   without sudo would stop reading them), `PrivateTmp` hides a Kerberos ticket cache in `/tmp` from the service (integrated authentication),
   and a `--repo` outside `/var/lib/dbdatasync`, never hardened at all so far, would start getting `ProtectSystem=strict` and the rest.
   Try it on a real Linux install before switching.
2. **Sync's Windows service account (slice 8).** `LocalSystem` stays the default (no `--allow-localsystem` needed), exactly as before. A virtual
   account (`NT SERVICE\DbDataSync`, what Proxy defaults to) is safer and `--account` already accepts it, but making it the default changes who
   owns the data directory and what integrated authentication connects as. `service doctor` reports the LocalSystem choice as a warning.
3. **Restart-on-failure for the Windows service.** Sync installs none, as before (`doctor` warns). The shared default
   (`FailurePolicy.Default`: restart after 5, 15 and 60 seconds) is a one-line change in `ServiceCommand.Spec`.
4. **Service start mode.** Automatic, as before; the shared default is delayed automatic.
5. **Product-specific text in a shared package.** `EnrollmentCompletionHandler`'s recovery message still names `dbdatasync config cert retrieve`.
6. **Sync does not instantiate the shared `CertCommand`** (slice 7): it registers its verbs on the shared `CertVerbs` table, so `show`, `ensure` and `check`
   are not available in Sync. Wiring them needs a `CertificateOptions` built from the resolved repository root.
7. **Event Log source name.** Host log entries now appear under source `DbDataSync` instead of `DbDataSync.Cli`; anyone filtering Event Viewer on the old name must change the filter.
8. **Not exercised by any CI run before the merge into `main`:** the `dist-*` installer workflows (they run only on pull requests into `main`) and the Docker image build (publish only). The group pull request into `main` is what runs them first.
9. **`AdcsCertificateEnroller` and `AdcsEnrollment`** have never run against a real enterprise CA (no CA in CI or in the sandbox where they were written).
