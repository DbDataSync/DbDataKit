# Binding a certificate that is already in the Windows store

For a certificate someone else put in `LocalMachine\My`: a platform team, group policy, a certificate you imported
yourself. Windows only. It does not need elevation, because it only reads the store and writes configuration.

```
dbdatasync config cert list
dbdatasync config cert bind --thumbprint <thumbprint>
```

`list` shows the server-authentication certificates in the store (`--location CurrentUser` to look in the user store
instead). A certificate without the server-authentication usage cannot serve TLS and is not listed.

`bind` writes four keys into `dbdatasync.config.yaml`, committed like any other config change:

| key | value |
| --- | --- |
| `Kestrel:Certificates:Default:Subject` | the certificate's common name. This is a store lookup, so no password or key is ever written to the file |
| `Kestrel:Certificates:Default:Store` | `My` |
| `Kestrel:Certificates:Default:Location` | `LocalMachine` |
| `Kestrel:Certificates:Default:AllowInvalid` | `true` for a self-signed certificate or with `--allow-invalid`; otherwise left as it was (`--no-allow-invalid` turns it off) |

Because the lookup is by subject, a new certificate with the same common name takes over on the next restart.

The service account must be able to read the private key. `dbdatasync config cert status` reports whether it can; the
issuing commands grant it for you, but a certificate you bound by hand may need the grant done in the store's private
key permissions.

Restart the service for it to take effect.
