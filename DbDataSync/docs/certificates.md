# Certificates

DbDataSync serves its API over HTTPS with one certificate (the dbdatakit console, which has the screens, pins it when it pairs with the server). There are four ways to get it, and the right one
depends on your platform and on whether you have a certificate authority (CA).

| I want... | Use | Platform | Renewed by |
| --- | --- | --- | --- |
| A certificate from my company's CA | [AD CS enrollment](certificates-ad-cs-enroll.md) | Windows, domain-joined | you, with `config cert renew` |
| A certificate that is already in the Windows machine store | [Bind an existing certificate](certificates-existing-windows-store.md) | Windows | whoever issued it |
| A certificate file I was given (certbot, an internal PKI, a platform team) | [PEM and PFX files](certificates-pem-and-pfx.md) | any | whoever issued it |
| Something working now, with no CA | [Self-signed](certificates-self-signed.md) | any | the service renews a file certificate itself; you renew a store certificate |

Every one of them ends the same way: `dbdatasync.config.yaml` gets the `Kestrel:Certificates:Default:*` keys, and
**nothing takes effect until the service restarts**.

- [Renewal and expiry](certificates-renewal-and-expiry.md): what renews what, and the warnings you will see.
- [Troubleshooting](certificates-troubleshooting.md): the errors you are most likely to hit.

The command reference is in [configuration.md](configuration.md#dbdatasync-config-cert-use-pemuse-pfxstatuslistnew-self-signedenrollrenewretrievetemplatesdiscoverbind);
the settings are under [`DbDataSync:Certificates:*`](configuration.md#dbdatasynccertificates) and
[`Kestrel:Certificates:Default:*`](configuration.md#kestrelcertificatesdefault).

`dbdatasync cert status` reports what is configured right now, on any platform.
