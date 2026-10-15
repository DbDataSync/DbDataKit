# DbData.Update

Checker, release sources, downloader, signature verification and safe extraction.

## Public contract
- Sources: `GitHubReleaseSource` (releases API, assets `release.json` + `release.json.sig`, injectable `HttpClient`/handler), `NuGetFlatContainerSource` (versions only, unsigned hints), `FolderReleaseSource` (folder/UNC, `<root>/<ver>/release.json[.sig]`), `UrlReleaseSource` (single "latest" manifest).
- `IContentFetcher` impls: `HttpContentFetcher` (Range resume, tolerates servers that ignore Range), `FileContentFetcher`, `CompositeContentFetcher`.
- `UpdateChecker.Evaluate(manifest, installed, options, product)` - pure policy: wrong product, revoked, channel not allowed, below known minimum-safe, updater too old, **downgrade refused unless `AllowDowngrade`**, mandatory when installed < `minimumSafeVersion` or is in `supersedes`. `CheckAsync` lists, picks the highest allowed version, fetches and verifies, then evaluates. A source that advertises version X but serves a signed manifest for Y throws `VersionMismatch` (replay protection).
- `UpdatePolicy.Decide(mode, decision, now, window)` -> `None | Notify | AwaitApproval | ApplyNow`.
- `Downloader` + `TrustedUrlPolicy`: artifact URL must be https (or file for folder sources), same host/port, no userinfo, no `..`/encoded separators, under a configured prefix. Downloads to `<sha256>.partial` (resumes), verifies size then sha256, renames to `<sha256>.bin`. File names derive from the signed hash, not the URL. Hash mismatch deletes the partial; truncation keeps it.
- `ReleaseVerifier.VerifyManifest(bytes, sigText, TrustStore, product)` - signature first, parse second. `VerifyFileAsync` for artifacts.
- `Ed25519`, `SignatureFile` (`dbdata-sig/1 <keyId> <base64>`), `ManifestSigner`, `TrustStore` (embedded keys; `Rotate(keyManifest)` accepts a manifest signed by a currently trusted key, with a strictly increasing sequence, may retire keys, can never empty the store), `ManifestBuilder`.
- `ArchiveExtractor.Extract` - sniffs zip/tar.gz by magic; rejects rooted paths, drive letters, `..` (either slash), NUL, link entries in tar, special tar types, too many entries, declared/actual size over limits. Zip entries are all validated before anything is written.

## Decisions
- ed25519: .NET 10's BCL has no ed25519. We use **BouncyCastle.Cryptography** (managed `Ed25519Signer`, RFC 8032), a widely audited package, so no native/libsodium dependency on any rid. Isolated in `Crypto.cs` so it can be swapped.
- Signature is detached over raw bytes (minisign-like), not JSON canonicalisation, avoiding canonicalisation bugs.
- Key rotation chain: the embedded key(s) are the root; a signed key manifest extends/retires. Signing key custody (protected CI environment/HSM) is a process decision, not code.
- Authenticode verification is optional and not implemented here (Windows-specific; add in the service updater).

## Threat notes
Tampered manifest/artifact, wrong key, unknown key id, replayed old manifest (version swap, `KnownMinimumSafeVersion`), downgrade, revoked release, URL-prefix tricks, zip-slip/tar-slip/links, zip bombs, torn downloads are each covered by tests. Not covered: compromise of the embedded keys themselves, and rollback to an older *validly signed* manifest when the client has no stored `KnownMinimumSafeVersion` (hosts should persist the highest seen).
