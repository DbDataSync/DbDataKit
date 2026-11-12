# DbDataKit.Connections.Files

The clients of the places a connection can keep files (plan 27a IX04 to IX06), behind one small interface, `IFileStore`: **list** a folder, **read** a file, **write** a file, **delete** one. `FileStores.OpenAsync(definition, secrets)` gives the store of a connection; `FileStoreTester.TestAsync` is **Test** (it connects as the file says, signs in and lists the root once, and says in words what it found).

| Kind | Protocol | Library | Lists | Pinned by |
| --- | --- | --- | --- | --- |
| `sftp` | SFTP over SSH | SSH.NET | yes | the host key's fingerprint, `file_server.host_keys` |
| `scp` | SCP over SSH (the file moved into place with `mv`, a folder made with `mkdir -p`) | SSH.NET | **no** (`Unsupported`: use `sftp` to list) | the host key, as `sftp` |
| `ftps` | FTP with TLS, explicit (AUTH TLS) or implicit | FluentFTP | yes | the certificate's SHA-256, `file_server.certificate_sha256`, or `tls.ca_file`, or the machine's roots |

## The connection file

```yaml
schema: connection/2
kind: sftp                      # sftp | scp | ftps
name: partner-drop
file_server:
  host: files.example.com
  port: 22                      # default 22; for ftps 21 (explicit) or 990 (implicit)
  root: /upload                 # every path is under it; a path that leaves it is refused
  host_keys: ["SHA256:..."]     # as `ssh-keygen -lf` prints it (ftps: certificate_sha256: ["<64 hex>"], ftps_mode: explicit|implicit)
sign_in:
  method: ssh_key               # ssh_key (sftp, scp) | basic (user, password_secret) | none (anonymous ftps)
  user: robot
  private_key_secret: secret:dbdatakit:server:connection:partner-drop:private_key
  # passphrase_secret: secret:dbdatakit:server:connection:partner-drop:passphrase
```

Every secret is a **reference** (the strict reader refuses a key written as itself); the console keeps a typed key in its secret store under `dbdatakit:server:connection:<name>:private_key`. `connection/2` is open: `kind`, `file_server`, the method `ssh_key` and its two keys are additive.

## Pinning: a changed server is refused by name

An SSH server is known by its **host key**, an FTPS server by its **certificate**. The reader **requires a pin** for an SSH server (`host_keys` is empty only with `tls: { verify: false }`, which says on purpose that any key is taken and is shown as a warning). On connect the key the server shows is compared with the pins **before any password or key is sent**; a key that is not pinned is refused with `PinMismatchException`, whose message names the server, the fingerprint it showed and the pinned ones, and what to do ("if the server was reinstalled, check the new fingerprint with its administrator and pin it; otherwise do not connect"). It is a permanent failure (kind Tls, code `host-key-refused` or `certificate-refused`). For FTPS a certificate is accepted when it is pinned (and then **only** a pinned one is), else when the machine's roots or `tls.ca_file` vouch for it; a self-made certificate nobody vouches for is refused naming its fingerprint.

Getting the first pin: **Test** with nothing pinned (and `tls.verify: false`) succeeds and says "the server shows SHA256:..., confirm it with its administrator, pin it and remove `tls.verify: false`". `FileStores.ProbeAsync` reads the fingerprint without signing in.

## A partial file is never picked up

`WriteAsync` writes `name.part` and renames it to `name` when the last byte is there (SFTP: posix-rename; FTPS: `RNFR`/`RNTO`; SCP: `mv`), making the folders above it. `ListAsync` leaves out names that look like a producer's own temporary file (`FileNames.IsTemporary`: `.part`, `.tmp`, `.temp`, `.partial`, `.filepart`, `.crdownload`, `.uploading`, a trailing `~`) unless `includeTemporary` is set. A file that a *foreign* producer writes under its final name is told by the caller by comparing `Size` and `Modified` across two looks (the file trigger, plan 27a IX07).

## Failures

Every failure is the shared `ConnectionFailure` (`FileFailures.Classify`), so a retry reads `IsTemporary`: a rejected login (`ssh-auth`, `ftp-530`), a missing path or file and a refused permission are permanent; no answer and a timeout are temporary. The library's own message is never passed on (it can hold a path or part of a key). A secret that is not set is `FileSecretNotSetException`, naming the reference.

## Limits of the thin wrapper

FTPS reads are buffered in memory (a very large file is better fetched with SFTP); SCP cannot list; implicit FTPS is written but is not proven against a server (the test server speaks explicit only).
