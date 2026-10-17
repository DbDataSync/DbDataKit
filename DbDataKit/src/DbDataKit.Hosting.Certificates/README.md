# DbDataKit.Hosting.Certificates

`CertificateProvider.LoadAsync(CertificateOptions)` by mode:
- `File`: PFX (password via `ICertificateSecretSource`) or PEM + key.
- `Store`: thumbprint or subject in LocalMachine\My (default); requires a private key and valid dates.
- `Enroll`: `ICertificateEnroller` interface only; the product supplies the ADCS implementation (Sync has one).
- `SelfSigned`: managed PFX, RSA 2048 / SHA-256, SANs = machine name, FQDN, localhost, 127.0.0.1, ::1 plus extras. Reused until inside `RenewBeforeDays` or until the required names change, then regenerated. A separate public `.cer` is written for client trust.

Decisions
- The PFX password is random (256-bit) and comes from the injected `ICertificateSecretSource.GetOrCreatePassword`; null/empty is rejected, so there is no empty-password PFX.
- The PFX is written to a temp file, passed to the `restrictFileAccess` callback (products plug in their ACL helper), then moved into place.
- `CertificateExpiry`/`CertificateReadinessProbe` surface rotation state on `/health` and doctor.
- `PlainHttpGuard.Enforce(urls, allowOverride)` refuses `http://` on any non-loopback bind (wildcards included).
