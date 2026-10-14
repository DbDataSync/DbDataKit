# Phase 204P — an installed certificate never has its private key exported

**Status:** built; verified by Windows tests below (no CA available, so the CA is stood in for by a certificate over a named key reduced to its public half). To be reviewed with the operator.

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

## Who calls `Install`, and how each path differs

Production callers of `CertificateStore.Install`:

| Caller | Key it hands in | Fails today? |
| --- | --- | --- |
| CLI `new-self-signed` (store target), `CertCommand.cs:314` | ephemeral, exportable (`RSA.Create`) | no |
| CLI `renew` with no CA configured (self-signed fallback), `CertCommand.cs:397` | ephemeral, exportable | no |
| Admin UI "issue self-signed", `AdminCertificateService.cs:111` | ephemeral, exportable | no |
| CLI `HandleEnrollmentResult`, `CertCommand.cs:783`, reached by `enroll`, `renew` (CA configured) and `retrieve` | named, persisted, **non-exportable** CNG key | **yes** |
| Admin UI `HandleEnrollmentResult`, `AdminCertificateService.cs:198`, reached by Admin enroll and Admin retrieve | same | **yes** |

The CLI `new-self-signed --file` / non-Windows path does not call `Install`. It writes a managed PFX or
PEM and needs an exportable key because the file is where the key is stored. It is unaffected.

How `enroll`, `renew` and `retrieve` differ, in terms that matter to this bug:

- **`enroll` and `renew` (CA configured)** both go through `SubmitAndHandle`: create a fresh named key
  in this process, build the CSR, submit. If the CA answers `Issued` immediately, the same process
  reopens the key and installs. **No `PendingEnrollmentStore` record has been written at that point**,
  because the store is only written for `Pending`. If `Install` throws, the request id exists only in
  memory and is lost.
- **`retrieve`** starts from a record written by an earlier `enroll`/`renew` that came back `Pending`.
  The key and the record are both still present, and both are deleted only *after* `Install`
  succeeds. If `Install` throws during `retrieve`, nothing is lost: the record and key remain, and
  running `retrieve` again works once the bug is fixed. (Whether `RetrievePending` can be called again
  for an already-collected request is unconfirmed.)
- All three reach the same `Install` with the same kind of key, so the crash itself is identical. The
  difference is only in what survives it: `retrieve` is recoverable, `enroll` and `renew` are not.
- The Admin UI has its own copy of this handler rather than calling the CLI's, so it has to be fixed
  separately today, and the two copies can drift.

## Decision: `Install` stops exporting keys, for every caller

`new-self-signed` into the Windows store is Windows-only, so the export-and-reload round-trip in
`Install` has no reason to survive for one caller while the others avoid it. Two code paths with two
export behaviours would leave a comment explaining something that is only true for historical reasons.

Instead, every certificate installed into the Windows store gets the same kind of key: a named,
persisted, non-exportable CNG key. `Install` no longer exports, reloads or creates anything; it adds a
certificate whose key is already in a named container, and refuses (with a clear exception) a
certificate whose key is ephemeral.

## Proposed fix

1. **Generalize the key helper.** `PendingEnrollmentKeys` already creates a named, persisted,
   non-exportable CNG key, but its name and docs are about pending enrollments. Rename or wrap it as a
   general key helper used by self-signed and enrolled certificates alike, keeping the pending-key
   naming for enrollment.
2. **Self-signed takes a key.** Add a `CertificateBuilder.CreateSelfSigned(spec, RSA key)` overload,
   as `CreateSigningRequest` already takes a key. The existing overload stays, because the cross-platform
   `--file` target needs an ephemeral exportable key and its tests run off Windows. The store target
   (CLI `new-self-signed`, CLI `renew` fallback, Admin issue self-signed) creates a named key, builds
   the certificate over it, and installs.
3. **`CertificateStore.Install`:** open the store first (keep that ordering and its reason), then
   `store.Add` the certificate that already carries its named key. Remove the PFX export, the random
   password and `LoadPkcs12`. Throw a clear exception for an ephemeral key. If anything fails after the
   key was created and before the certificate is referenced by the store, delete the key so it is not
   orphaned. Keep the friendly name: commit 82fa0ba made `Install` copy `FriendlyName` onto the reloaded
   certificate because the PFX round trip dropped it. With no round trip there is nothing to drop, but
   set `FriendlyName` on the certificate before `store.Add` and keep that commit's test passing.
4. **Write the request id before installing, for any outcome that has one.** In the shared handler, as
   soon as the CA returns an issued or pending certificate, save the `PendingEnrollmentStore` record
   (request id, key name, subject, SANs, CA). Remove it only after `Install` and the key-access grant
   succeed. This makes `enroll`, `renew` and `retrieve` behave the same: a failure anywhere after the CA
   issued leaves a record, so `dbdatasync config cert retrieve --request-id <id>` can finish the job.
   On failure, catch the exception and print the request id with that exact command, instead of letting
   the stack trace escape.
5. **One shared handler.** Move the `Issued`/`Pending`/failed handling out of both
   `CertCommand.HandleEnrollmentResult` and `AdminCertificateService.HandleEnrollmentResult` into one
   method in `DbDataSync.Certificates` that both call, returning a result each caller formats (console
   text for the CLI, `CertificateActionResult` for the Admin UI). Fixing the Admin UI then needs no
   second copy of the fix.
6. **Reconcile the comments** in `PendingEnrollmentKeys`, `CertificateBuilder` and
   `CertificateStore.Install`. After this change "no private key is ever exported" is true for the
   store path.
7. **Confirm** `PrivateKeyAccess.Grant` works on a certificate whose key was installed this way. It
   reads the key file by the CNG unique name, which suggests it works, but it has never been run
   against an installed enrolled certificate because that install never succeeded.


## Tests

- `Category=Windows`, no CA, no elevation: create a named non-exportable key in `CurrentUser`, build a
  self-signed certificate over it, `Install` it. Reproduces the reported failure with the old code
  path, passes after. Also asserts the installed certificate's key is usable for signing, is still
  non-exportable, and that `PrivateKeyAccess.Grant` succeeds.
- Same for a certificate with a key attached via `CopyWithPrivateKey`, the shape the enrollment path
  produces (a fake `Issued` result stands in for the CA).
- `Install` given an ephemeral key throws the clear exception and leaves nothing in the store.
- Failure path: `Install` throwing after `Issued` leaves the pending record and the named key in
  place, the caller prints the `retrieve --request-id <id>` command, and no unhandled exception escapes.
  Run it through the shared handler for both the CLI and Admin callers.
- Success path removes the record but **keeps the key**: it is the installed certificate's private key.
- The existing `Install` tests (`CertificateStoreWindowsTests`, `AdminCertificateServiceWindowsTests`,
  `CertificateExpiryServiceWindowsTests`) currently pass an ephemeral certificate. They must be
  rewritten to use a named key, since `Install` will refuse an ephemeral one.
- The cross-platform `--file` self-signed tests are unchanged.

## Open questions

- Is calling `RetrievePending` again for an already-issued request safe? Step 4 depends on it for the
  recovery path. Needs a check against the COM `ICertRequest` documentation or a real CA.

## What was built

- `PendingEnrollmentKeys` is now `CertificateKeys`, used for every store certificate. Key names are
  `DbDataSync-<guid>` (previously `DbDataSync-PendingEnrollment-<guid>` for enrollment only); the pending
  record stores whatever name it was given, so old records still resolve.
- `CertificateBuilder.CreateSelfSigned(spec, RSA key)`; the one-argument overload is unchanged and still
  serves the `--file` target and the cross-platform tests.
- `CertificateStore.Install` no longer exports or reloads: it refuses a certificate whose key is not a named
  CNG key, adds the certificate, and returns the store's copy. `CertificateStore.InstallSelfSigned` creates
  the key, builds the certificate over it and installs, deleting the key if that fails. The CLI
  `new-self-signed`, the CLI `renew` fallback and the Admin "issue self-signed" all use it.
- `EnrollmentCompletionHandler.Complete` (`DbDataSync.Certificates`) is the one handler for `enroll`,
  `renew`, `retrieve` and the Admin screen. It saves the request record *before* installing and removes it
  only after install and the key-access grant succeed; on failure it returns the exact
  `dbdatasync config cert retrieve --request-id <id>` command in its message. Both copies of the old
  handler are gone.
- An enrolled certificate now gets the spec's friendly name ("DbDataSync"), which only self-signed ones had.

## Corrections to the design above

- **The key is not deleted on success.** The design said the success path removes the record and the key.
  That was wrong for the new shape: the named key *is* the installed certificate's key, so deleting it would
  destroy the certificate's key. The old code could delete it because `Install` had made a second copy by
  round-tripping. Only a denied or failed request, or an install that threw before the store referenced the
  certificate (`InstallSelfSigned`), deletes it.
- The assumption that `store.Add` on a certificate carrying a named key keeps the link to it **held**:
  tested on Windows, the installed certificate signs, is not exportable, and `PrivateKeyAccess.Grant` works.

## Tests

`InstalledKeyWindowsTests` (all `Category=Windows`, `CurrentUser`, no elevation): the premise (a PFX export of a
non-exportable key throws the reported message); a named non-exportable key installs; the installed key signs and
stays non-exportable; the `CopyWithPrivateKey` shape a CA answer has installs and accepts a key-access grant; an
ephemeral key is refused and leaves nothing; `InstallSelfSigned` keeps the friendly name; the handler's four
outcomes (installed, install-failed keeps record and key and names the retrieve command, pending, denied deletes
the key). The three existing `Install` helpers were switched to `InstallSelfSigned`.

## Not verified

Against a real CA: nothing here talks to AD CS, so `AdcsEnrollment.Submit`/`Retrieve` and the real shape of an
issued certificate are as untested as before this change. Whether `RetrievePending` can be called again for an
already-issued request is still unconfirmed, and the recovery path for a failed install depends on it. Run on a
real `enroll` before calling this done.
