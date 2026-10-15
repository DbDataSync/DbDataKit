# Certificate files: PEM and PFX

For a certificate you were handed as a file, on any platform: certbot, an internal PKI, a platform team.

```
dbdatasync config cert use-pem --cert /path/fullchain.pem --key /path/privkey.pem
dbdatasync config cert use-pfx --pfx /path/server.pfx
```

Each writes `Kestrel:Certificates:Default:Path` (and `KeyPath` for PEM) into `dbdatasync.config.yaml`, which Kestrel
reads directly. Add `--repo <path>` if you are not in the configuration directory.

Limits:

- **The key must not be encrypted.** An encrypted PEM key is refused, and `use-pfx` takes a PFX with no password.
  Decrypt it first, for example `openssl rsa -in encrypted.key -out privkey.pem`, or re-export the PFX without a
  password. Protect the file with its permissions instead.
- **DbDataSync does not renew these.** Whatever issued the file renews it; replace the file and restart the service.
  The service never touches a certificate at a path other than its own managed self-signed one.

`dbdatasync config cert status` reports the path, names, expiry and days remaining.

Restart the service for it to take effect.
