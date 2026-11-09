# DbDataKit.Upgrade

Upgrades from the console (plan 24a UC08 and UC09). A library and the endpoints of a server's management API; the console reads the release feed with it and a server upgrades itself through it.

## What the console shows (UC08)

`ReleaseFeed` lists the releases of the suite: the GitHub releases of `DbDataSync/DbDataKit` (tags `<product>/v<version>`, CalVer `YYYY.M.D.HHmm`), or a **mirror** for a site without internet. It reads the checksums of one release from its `SHA256SUMS` and, when the release has one, the compatibility generation it was built with from its `release.json` (`release/1`, `schemaVersions.generation`). It never downloads a program.

The console's setting is `upgrade.feed` in `settings.yaml` of its directory:

```yaml
upgrade:
  feed:
    mirror: D:\releases        # a folder or an https address; without it the GitHub releases are read
    repo: DbDataSync/DbDataKit
    channel: stable            # stable (default), beta or snapshot
```

A mirror folder is laid out `<product>/v<version>/` with the release's files in it (the zips, the MSI, `SHA256SUMS`), or holds a `releases.json` (what `GET /repos/<repo>/releases` answers, saved) with the files beside it; a mirror address serves that `releases.json`. A key that is not a setting, a folder not named for a version, a `SHA256SUMS` line that is not a checksum, or a `releases.json` that is not a list is refused with the file's name (and the line). A server reads its own feed from the variables `DBDATA_UPGRADE_MIRROR`, `DBDATA_UPGRADE_REPO` and `DBDATA_UPGRADE_CHANNEL` (set them in its service or unit), because a product reads its own files strictly.

The console's **Upgrades** screen (`/upgrades`) lists the versions available per product and each paired server's current and newest, with "Newer available" on the Servers page, and marks a release built outside N and N-1 of the console ("outside N-1 after this release", with both versions and generations and the side to upgrade).

A server upgrading itself from this feed is UC09 (the next slice).
