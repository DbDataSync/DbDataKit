# DbDataKit.Connections.Files

The clients of the places a connection can keep files (plan 27a IX04 to IX06), behind one small interface, `IFileStore`: **list** a folder, **read** a file, **write** a file, **delete** one. `FileStores.OpenAsync(definition, secrets)` gives the store of a connection; `FileStoreTester.TestAsync` is **Test** (it connects as the file says, signs in and lists the root once, and says in words what it found).

| Kind | Protocol | Library | Lists | Pinned by |
| --- | --- | --- | --- | --- |
| `sftp` | SFTP over SSH | SSH.NET | yes | the host key's fingerprint, `file_server.host_keys` |
| `scp` | SCP over SSH (the file moved into place with `mv`, a folder made with `mkdir -p`) | SSH.NET | **no** (`Unsupported`: use `sftp` to list) | the host key, as `sftp` |
| `smb` | a Windows share, signed in to as the Windows identity of the process (Kerberos); Windows only | `System.IO` over `\\host\share` | yes | Windows (the server's name; Kerberos proves it) |
| `s3` | the S3 API: Amazon and S3-compatible servers (MinIO, Ceph), signed with Signature Version 4, no SDK | `HttpClient` | yes (a folder is a key prefix) | TLS (`tls.ca_file`, `tls.verify`) |
| `azure-blob` | Azure Blob Storage, signed with Shared Key or a SAS token, no SDK | `HttpClient` | yes (a folder is a blob-name prefix) | TLS |
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

## SMB with Windows sign-in (plan 27a IX05)

```yaml
kind: smb
file_server: { host: fs01.corp.example.com, share: data, root: /incoming/partner }   # \\fs01.corp.example.com\data\incoming\partner
sign_in: { method: kerberos }                                                         # no secret: the Windows identity of the process
```

`SmbFileStore` reaches the UNC path with `System.IO`; Windows asks the domain controller for a Kerberos ticket for the server's **name** (an address makes it fall back to NTLM: the file reader says so as a warning) and signs and encrypts as the server's policy says. On a host that is not Windows, `FileStores.OpenAsync` is the classified failure `smb-needs-windows` (kind Unsupported) and **Test** says so in words. `SmbFailures` classifies what Windows reports by its numbers (a failed logon, no logon server, a Kerberos ticket that could not be had: a rejected login; a clock skew; an unreachable server or share: the network; access denied: a permission); `PathFileStore` is the same store over any folder, which is what Linux tests exercise. Only a Windows host in a domain proves the ticket and the share themselves.

## Object storage (plan 27a IX06, decision SD13: S3-compatible and Azure Blob)

```yaml
kind: s3                                   # or azure-blob
endpoint: https://s3.eu-west-1.amazonaws.com   # a MinIO or Ceph server: its address, and object_store.path_style: true
object_store: { bucket: reports, prefix: incoming/, region: eu-west-1 }   # azure-blob: bucket is the container
sign_in: { method: access_key, user: AKIA..., password_secret: secret:dbdatakit:server:connection:reports:password }
# azure-blob: { method: shared_key, user: <account>, key_secret: ... }  or  { method: sas_token, key_secret: <the token> }
```

Every request is one HTTP call signed by the library itself (`AwsSigV4`, `AzureSharedKey`): the signers are checked against the examples Amazon documents (with their known signatures) and, for Azure, against Azurite, which verifies every Shared Key signature. The key is a secret reference; the address has no query. A folder is a key prefix (a listing uses the delimiter and follows the continuation token); `object_store.prefix` is the folder every path is under. An object is put **whole in one request, so it exists under its name only when complete** (no `.part`): the body is buffered in memory, so a single object is limited by memory and by 5 GB for S3 (multipart upload is not built). A missing object, bucket or container is the permanent failure `NotFound`; `403` is a rejected login when the service's own code says the key or signature is wrong (`InvalidAccessKeyId`, `SignatureDoesNotMatch`, `AuthenticationFailed`), else a missing permission; `429` and `5xx` are temporary. A delete of an S3 object that is not there succeeds (as S3's does); on Azure it is `NotFound`. `http` to a server that is not this machine is a warning. Google Cloud Storage is not in scope (SD13).

## Pinning: a changed server is refused by name

An SSH server is known by its **host key**, an FTPS server by its **certificate**. The reader **requires a pin** for an SSH server (`host_keys` is empty only with `tls: { verify: false }`, which says on purpose that any key is taken and is shown as a warning). On connect the key the server shows is compared with the pins **before any password or key is sent**; a key that is not pinned is refused with `PinMismatchException`, whose message names the server, the fingerprint it showed and the pinned ones, and what to do ("if the server was reinstalled, check the new fingerprint with its administrator and pin it; otherwise do not connect"). It is a permanent failure (kind Tls, code `host-key-refused` or `certificate-refused`). For FTPS a certificate is accepted when it is pinned (and then **only** a pinned one is), else when the machine's roots or `tls.ca_file` vouch for it; a self-made certificate nobody vouches for is refused naming its fingerprint.

Getting the first pin: **Test** with nothing pinned (and `tls.verify: false`) succeeds and says "the server shows SHA256:..., confirm it with its administrator, pin it and remove `tls.verify: false`". `FileStores.ProbeAsync` reads the fingerprint without signing in.

## A partial file is never picked up

`WriteAsync` writes `name.part` and renames it to `name` when the last byte is there (SFTP: posix-rename; FTPS: `RNFR`/`RNTO`; SCP: `mv`), making the folders above it. `ListAsync` leaves out names that look like a producer's own temporary file (`FileNames.IsTemporary`: `.part`, `.tmp`, `.temp`, `.partial`, `.filepart`, `.crdownload`, `.uploading`, a trailing `~`) unless `includeTemporary` is set. A file that a *foreign* producer writes under its final name is told by the caller by comparing `Size` and `Modified` across two looks (the file trigger, plan 27a IX07).

## Failures

Every failure is the shared `ConnectionFailure` (`FileFailures.Classify`), so a retry reads `IsTemporary`: a rejected login (`ssh-auth`, `ftp-530`), a missing path or file and a refused permission are permanent; no answer and a timeout are temporary. The library's own message is never passed on (it can hold a path or part of a key). A secret that is not set is `FileSecretNotSetException`, naming the reference.

## Limits of the thin wrapper

FTPS reads are buffered in memory (a very large file is better fetched with SFTP); SCP cannot list; implicit FTPS is written but is not proven against a server (the test server speaks explicit only).
