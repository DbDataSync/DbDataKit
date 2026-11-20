# INSTALLER-11 (work in progress)

Branch `backup/installer-11`, started from `backup/installer-4` (c9d9dc5) so the INSTALLER branches stay one line.

## Audit against the code

Nothing of the join exists yet: no join token, join request, `join-request.yaml` or `/api/v1/join` in the code. What is reused: `ConsoleSigningKey`
(Ed25519), `Base64Url`, `PairingProof.CertificateFingerprint`, the pinned TLS handler of `PairingClient`, `IAuditLog`/`AuditEntry`, `ServerRoles`,
`SuiteCompatibility`, `StrictYamlReader`.

## Built so far

- (nothing yet)

## Open questions

## Defaults taken
