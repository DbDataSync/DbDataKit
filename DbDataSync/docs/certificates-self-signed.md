# Self-signed certificates

A certificate this tool makes itself. Fine for a first run, a lab, or a single operator; every browser or client that
reaches the console has to be told to trust it once, because no CA stands behind it.

There are two places the certificate can live.

## A file (any platform)

```
dbdatasync config cert new-self-signed
```

This is the default off Windows, and `--file` asks for it on Windows too. It writes `<repo>/tls/dbdatasync.pfx` and
points Kestrel at it, and a `dbdatasync.cer` beside it that clients can import to trust it. The names on it are the
machine's name, `localhost`, the loopback addresses and the console URL's host; `--url` overrides that host, and
`--days` sets how long it is valid (default 397).

The PFX is encrypted with a random password kept in `dbdatasync.pfx.pwd` next to it (encrypted with DPAPI on Windows,
a file readable only by the service's user elsewhere). The password is never written to the configuration, which is
committed to git; the service reads it at start. The whole `tls/` directory is git-ignored.

The running service **renews this file itself**, in place, once the certificate is within 30 days of expiring or no
longer covers the console URL's host, and raises a notification saying a restart is needed to pick it up. It only ever
touches this one file; a certificate you supplied with `use-pem` or `use-pfx` is never replaced.

**Upgrading from an earlier version:** older versions wrote the PFX with no password. That file keeps being served until
the next renewal check, which replaces it with a new certificate (a new key, so clients that trusted the old one must
trust the new `.cer`). To do it straight away, run `dbdatasync config cert new-self-signed` and restart.

Needs no elevation.

## The Windows machine store

```
dbdatasync config cert new-self-signed --dns dbdatasync.example.com
```

From an **elevated** prompt, on Windows (`--store` states it explicitly). `--dns` is required and takes one or more
names, comma-separated; the first is the subject. `--days` and `--account` are as for [enrollment](certificates-ad-cs-enroll.md).

The certificate is installed in `LocalMachine\My` with a non-exportable private key. The key is never exported, not even
briefly. Then:

```
dbdatasync config cert bind --thumbprint <thumbprint>
```

`bind` writes `Kestrel:Certificates:Default:AllowInvalid: true` for a self-signed certificate, because Kestrel refuses a
certificate whose chain it cannot build. It says so when it does.

Nothing renews a store certificate for you. `dbdatasync config cert renew`, with no CA configured, issues a fresh
self-signed one with the same subject and names. See [renewal and expiry](certificates-renewal-and-expiry.md).
