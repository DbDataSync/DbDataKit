# DbData.Update.Tools (`dbdata-update`)

dotnet tool for CI: create, sign and verify `release.json`.

```
dbdata-update keys generate  --key-id 2026-a --out-private k.key [--out-public k.pub]
dbdata-update keys rotate    --sequence 1 --signer-id 2026-a --key-file k.key --add 2027-a=<b64pub> [--retire 2026-a] --out keys.json
dbdata-update manifest create --product dbdataproxy --version 1.4.0 --artifacts dist/ --base-url https://github.com/o/r/releases/download/v1.4.0
                              [--channel stable] [--min-updater 1.0] [--requires-config-migration] [--schema-version config=3]
                              [--supersedes 1.3.0] [--minimum-safe-version 1.3.5] [--revoked] [--notes ...] [--out dist/release.json]
dbdata-update manifest sign   --manifest dist/release.json --key-id 2026-a [--key-file k.key]   # or env DBDATA_SIGNING_KEY
dbdata-update manifest verify --manifest dist/release.json --trusted-key 2026-a=<b64pub> [--product p] [--artifacts dist/]
```
Artifacts: every file in `--artifacts` except `release.json*`/`SHA256SUMS`; rid and kind inferred from the file name (`win-x64`, `.zip`, `.tar.gz`, `.nupkg`, `.mcpb`). Exit 0 ok, 1 failure.

## GitHub reusable workflow sketch
```yaml
# in the protected "release-signing" environment only
- run: dotnet tool install -g DbData.Update.Tools
- run: dbdata-update manifest create --product "$PRODUCT" --version "$VERSION" --artifacts dist --base-url "https://github.com/${{ github.repository }}/releases/download/v$VERSION"
  env: { PRODUCT: dbdataproxy, VERSION: ${{ inputs.version }} }   # tag/version only through env:, never interpolated into the script
- run: dbdata-update manifest sign --manifest dist/release.json --key-id "$KEY_ID"
  env: { DBDATA_SIGNING_KEY: "${{ secrets.DBDATA_SIGNING_KEY }}", KEY_ID: 2026-a }
- run: dbdata-update manifest verify --manifest dist/release.json --trusted-key "2026-a=$PUBKEY" --artifacts dist   # self-check before publishing
```

## Notes
The private key is never written by the tool except `keys generate` (refuses to overwrite; 0600 on Unix). Prefer the env var in CI so the seed never touches disk. SBOM/provenance attestation is a workflow concern, not this tool.
