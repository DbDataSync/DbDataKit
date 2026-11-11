# Shared TLS / certificate / service rollout

Status: in progress (follow-ups open; low priority, owner 2026-11-09; re-audited 2026-11-11, nothing built or changed since: the owner's review of this plan is still to come). The rollout slices 0 to 11 are built and merged; their history is in git.

- Certificates, the Windows store, AD CS enrollment, service control and the systemd and Docker generators live in `DbDataKit.Hosting.Certificates*`, `Hosting.WindowsService`, `Hosting.Systemd` and `Hosting.Cli`; Sync and Proxy use them (Sync's `config cert` verbs register on the shared `CertVerbs` table).
- A managed self-signed PFX is encrypted with a random password in a protected file; there is no migration of an older unencrypted file (a documented breaking change, `DbDataSync/docs/certificates-self-signed.md`); renewal is `RenewBeforeDays` (default 30).
- Sync refuses plain HTTP from a non-local client unless it came over HTTPS or through a trusted TLS-terminating proxy (`DbDataSync:App:TrustedProxies`); the Docker images default `AllowInsecureHttp=true`, a deployment behind a proxy sets it back to `false`.
- Sync's Windows `service install|uninstall|status|start|stop|restart|doctor` run on the shared `ServiceProvisioner` (the real service-control manager is exercised by `DbDataSync/tools/service-e2e.ps1` in the Windows job).
- Not exercised by any CI run before a merge into `main`: the `dist-*` installer workflows and the Docker image build; the owner reviews this plan in detail later.

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
