# Renewal and expiry

| Certificate | Renewed by | You do |
| --- | --- | --- |
| Managed self-signed file (`new-self-signed`, off Windows or with `--file`) | the service, in place, once it is within a third of its lifetime of expiring | restart when the notification says so |
| Windows store, from a CA | you, with `dbdatasync cert renew` from an elevated prompt | renew, then restart |
| Windows store, self-signed | you, with `dbdatasync cert renew` when no CA is configured | renew, then restart |
| A file you supplied (`use-pem`, `use-pfx`) | whatever issued it | replace the file, restart |
| A store certificate you bound that someone else manages | them | restart after they replace it |

`renew` reads the currently bound certificate and re-enrolls with the same subject and names. With a CA and template
configured (see [enrollment](certificates-ad-cs-enroll.md)) it asks the CA; with none it makes a fresh self-signed one. Either way it
installs the new certificate, but the service keeps serving the old one until restarted, because Kestrel resolves its
certificate once at startup. A restart interrupts any replication run in progress, so it is your call when.

## The warnings

On Windows the service checks the bound certificate daily. It raises a `CertificateExpiring` notification, at most once a
day, starting `DbDataSync:Certificates:ExpiryWarningDays` days (default 30) before `NotAfter`, and `CertificateExpired`
once it has passed.

`dbdatasync cert status` shows the same dates and whether the service account can read the private key.
