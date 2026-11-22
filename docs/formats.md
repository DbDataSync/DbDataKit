# File formats
<!-- reviewed: 2026-11-20 -->

Each service reads versioned files: files a person edits (`task.yaml`, a model, a connection, a manifest) and published definitions. This page says
which versions are read, which files are refused, and what is written.

## Upgrading

**The suite is still making breaking changes.** A new release may not read the files an earlier release wrote. To upgrade, set the install up again.
A service that finds a file it cannot read refuses to start, or to load that file, and the message names the file.

**Once breaking changes stop**, an upgrade installs the new release over the old one. Each service reads the files the previous release wrote (see
"Which versions are read"), and the next save writes the current version.

Your own data, such as your databases and DuckDB files, is never changed by an upgrade.

## Which versions are read

Each kind of file has a current version and a previous version, listed in `docs/formats.json`.

- **A file from the previous version up to the current version loads as it is.** The previous version is the current version minus one, unless the
  kind's entry in `docs/formats.json` lists an older one.
- **An older file is read as written.** If someone adds a version 2 section to a version 1 file by hand, the service does not check for it.
- **A file older than the previous version is refused.** The message says what changed and what to do.
- **A file newer than the current version is refused.** The message says to update the service. A service that knows only version 1 refuses a
  version 2 file.

## What is written

- **Every save writes the current version.** A file loaded as version 1 is saved as version 2, whichever service saves it.
- **A publish sends the current version.** The file is loaded, checked and written out in the current version, and that text is what is published.
  The file on disk keeps the person's comments and layout.
- **A service that reads published definitions reads the current version and the one before.** Upgrade the services that read published definitions
  before the ones that publish them.
- **A restore from history writes the commit's files as they were.** The next save writes the current version. A restored file older than the previous
  version is refused when it is loaded, and the message names the file.

Example: Sync's `task.yaml` is at version 2, which adds the optional `checks:` section. A replication whose files say `schemaVersion: 1` loads
unchanged. The first save in the Workspace writes `schemaVersion: 2`. A publish to a Runner sends version 2, whether or not the file was saved.
