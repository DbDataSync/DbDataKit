# DbDataKit.Secrets.Store

The shared secret store (plan 13, W13): secrets encrypted in a file, with the key kept where the platform keeps keys safe. It works the same for a service, a command line and a container and needs no operating-system keychain (which is per user and does not suit services).

## The store

`EncryptedFileSecretStore` is `<data>/secrets/secrets.yaml`: `name -> { v: 1, key: <key id>, value: <base64 of a 96-bit nonce, the ciphertext and the 128-bit tag>, updated }`. It is outside the git-tracked configuration directory (or ignored by it), so no secret, even encrypted, is in a history; configuration files hold only references (`secret:dbdata:gateway:datasource:erp:password`).

- **Cipher**: AES-256-GCM, a fresh random nonce per write, the secret's **name as associated data**, so a value copied to another name fails to decrypt.
- **Writes** are atomic (a restricted temporary file replaces the store) under a lock that holds across processes; the file is restricted before content is written.
- **Reading**: it is an `ISecretProvider` and answers `secret:` references in the resolver chain ahead of `env:` and `file:`; inline `dpapi:` values resolve too (Windows).
- **Rotation** (`secret rotate-key`): a new data key, every value encrypted again, the old key deleted after the last value is rewritten; an interrupted rotation is finished by the next run.
- **Moving a server** (`secret export` and `secret import`): the values are re-encrypted under a passphrase (PBKDF2-SHA256, 600,000 rounds, random salt, name as associated data; an export that says fewer rounds is refused). The key file is never copied by a command.
- **No command prints a value** (see `DbDataKit.Secrets.Cli`).

## Where the key lives

| | |
| --- | --- |
| Windows | DPAPI (`DbDataKit.Secrets.Windows.DpapiKeyProtector`, per-product entropy, machine scope for a service) in `<data>/secrets/keys/<id>.key`, the directory restricted by access list to the service account, SYSTEM and Administrators |
| Linux, macOS | the raw key in `<data>/secrets/keys/<id>.key`, mode 0600 in a 0700 directory, **owned by the user the server runs as** |
| From outside | `secrets.keyFile` in `server.yaml`, or the environment variable `DBDATA_SECRETS_KEY_FILE`: a key the platform provides (a Docker or Kubernetes secret, a systemd credential), as 32 raw bytes or base64 or hex text. Images never contain a key. The file's permissions are not judged: a mounted secret is the platform's |

`KeyRing` refuses to read a key file or directory that others can reach (`KeyPermissionException`, which names the path and the `chmod` that fixes it), the way ssh refuses a private key;
`SecretKeys.Open` checks the whole ring at start-up. A key has an id, kept beside every value it encrypts, so an old key stays until the last value has moved to a new one.
A key file made by one kind of protection is never read as another (a DPAPI key on a machine without it, a raw key where DPAPI is expected).

The file's owner is asked of the system's `stat` and `id` (.NET has no call for it); where they are missing the mode check alone stands.

## Backup

Back up the key (the file in `keys/`, or the platform's key) **separately** from `secrets.yaml`: either one alone is useless. A DPAPI key file cannot be copied to another machine; moving a
server goes through `secret export` and `secret import`.

Machine-scope DPAPI is not a boundary between processes on one host: any process of the machine that has the entropy can decrypt, so the access list on `<data>/secrets` is the control.
