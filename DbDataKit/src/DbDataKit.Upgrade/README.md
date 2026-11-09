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

## A server upgrades itself (UC09)

`GET /api/v1/upgrade` (viewer) is the status the console polls; `POST /api/v1/upgrade` (admin) takes `{"action": "prepare"}` or `{"action": "apply"}`. Both are audited, refusals too.

- **prepare** downloads the zip for this platform from the feed's trusted prefix, checks it against the release's `SHA256SUMS` (size and hash; a tampered download is refused and nothing is staged), checks the compatibility generation against the paired servers, stages the release as a `release/1` manifest signed with the server's own key and an `update-plan/1` journal, and reports what will happen. Nothing about the running server changes.
- **apply** drains (`IDrainCoordinator`: stop taking new work, wait for the work running, 10 minutes at most; past that nothing is changed and the server takes work again: running work is never stopped silently), then starts the helper, which stops the service, moves the program folder aside and the new one into its place, starts the service, waits for the new version to answer `/version` ready, and commits, or puts the old version back and says why. The status names the state (`none`, `prepared`, `draining`, `applying`, `committed`, `rolled_back`, `rejected`, `failed`) and the steps of the journal; the old version stays whole in `<program folder>.upgrade/versions/`.
- An **image**, a **Scoop**, **WinGet** or **tool** install is not upgraded here: `prepare` returns the command and nothing is downloaded. A server **started by hand** (not a service or a unit) is shown the install script's command.

It reuses the shelved updater's pieces as code (`DbDataKit.Update`: the `release/1` manifest, the downloader with its trusted-prefix policy, the archive extractor; `DbDataKit.Update.Service`: `UpdateHelper`'s swap with rollback and `update-plan/1`; `DbDataKit.Update.Detect`: how it was installed), without the updater as a product or a service.

### Needs of an install

The helper is a copy of the old program run with `--dbdata-upgrade-helper` (a product's `Main` calls `UpgradeHelperHost.TryRunAsync(args)` first), under `systemd-run` where the server is a unit, so it outlives the service it stops. It runs with the rights of the service: the account must be allowed to rename its program folder (so to write its parent) and to stop and start the service. The default layouts keep the program folder read-only for the service, and then `prepare` says so and shows the command. The service's own account must be able to run `systemctl` or `sc.exe` on itself.

Not done: the Windows MSI path (an MSI install is swapped as a folder), DbDataSync, running a state migration from the helper, and the console driving several servers in order (UC10).
