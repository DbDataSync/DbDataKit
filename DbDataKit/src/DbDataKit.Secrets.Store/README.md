# DbDataKit.Secrets.Store

The shared secret store (plan 13, W13): secrets encrypted in a file, with the key kept where the platform keeps keys safe. This package so far holds the **keys**; the encrypted file store
(`EncryptedFileSecretStore`), rotation, export/import and the `secret` commands are the next slices.

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
