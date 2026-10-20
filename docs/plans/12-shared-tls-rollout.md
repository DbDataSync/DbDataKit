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
