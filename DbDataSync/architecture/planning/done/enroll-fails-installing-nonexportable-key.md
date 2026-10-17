# `config cert enroll` fails installing the issued certificate (non-exportable key)


**Resolved:** diagnosed, and the design agreed. The work lives in
`architecture/implementation/todo/phase-204P-installed-certificate-never-exports-its-key.md`; this file keeps the
original report and diagnosis.

## Observed

`dbdatasync config cert enroll` with a valid DNS name, CA and template. The CA accepted the request,
then the CLI crashed with an unhandled exception:

```
System.Security.Cryptography.CryptographicException: Key not valid for use in specified state.
   at ...X509Certificate.Export(X509ContentType contentType, String password)
   at DbDataSync.Certificates.CertificateStore.Install(X509Certificate2 certificate, StoreLocation location)  (CertificateStore.cs:53)
   at DbDataSync.Cli.CertCommand.HandleEnrollmentResult(...)  (CertCommand.cs:782)
   at DbDataSync.Cli.CertCommand.SubmitAndHandle(...)  (CertCommand.cs:764)
   at DbDataSync.Cli.CertCommand.Enroll(...)  (CertCommand.cs:347)
```

## Cause

- `PendingEnrollmentKeys.Create` makes the enrollment key non-exportable
  (`ExportPolicy = CngExportPolicies.None`). That is deliberate: nothing in the feature should export a
  private key.
- On `Issued`, `HandleEnrollmentResult` reopens that key and attaches it with `CopyWithPrivateKey`, then
  calls `CertificateStore.Install`.
- `Install` round-trips the certificate through a PFX (`certificate.Export(X509ContentType.Pfx, ...)`,
  then `LoadPkcs12` with `PersistKeySet`). Exporting a PFX with the private key needs an exportable key,
  so a non-exportable key throws "Key not valid for use in specified state".
- Self-signed certificates do not hit this because their key is exportable and ephemeral. Only
  `enroll` / `renew` / `retrieve` use the non-exportable named key. `PendingEnrollmentKeys` documents
  that this path was "not tested against a real CA", and the existing key-lifecycle test never installs
  a certificate with such a key.

The `PendingEnrollmentKeys` and `Install` comments both assert "no private key is exported", but
`Install` does export, so the two decisions contradict each other for this path.

## Side effects of the failed run

- The CA holds an issued certificate that was never installed. Re-running `enroll` issues a second one,
  so the first may need revoking.
- The named key `DbDataSync-PendingEnrollment-<guid>` stays in the machine key store: it is deleted only
  after a successful install.
- No `PendingEnrollmentStore` record exists, because the outcome was `Issued`, not `Pending`.
  `config cert retrieve` therefore cannot collect that certificate.
- The crash is unhandled (raw stack trace), unlike the `Submit` failure path, which prints
  "Enrollment failed: ...".

Manual cleanup of the orphaned key from an elevated prompt (untested):
`certutil -delkey -csp "Microsoft Software Key Storage Provider" <keyname>`; names are listed by
`certutil -key -csp "Microsoft Software Key Storage Provider"`.
