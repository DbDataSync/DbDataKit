# Self-signed certificates

A certificate this tool makes itself. Fine for a first run, a lab, or a single operator; every browser or client that
reaches the console has to be told to trust it once, because no CA stands behind it.

There are two places the certificate can live.

## A file (any platform)

```
dbdatasync config cert new-self-signed
```

This is the default off Windows, and `--file` asks for it on Windows too. It writes `<repo>/tls/dbdatasync.pfx` and
points Kestrel at it. The names on it are the console URL's host plus `localhost`; `--url` overrides the host, and
`--days` sets how long it is valid (default 397).

The running service **renews this file itself**, in place, once the certificate is within a third of its lifetime of
expiring, and raises a notification saying a restart is needed to pick it up. It only ever touches this one file; a
certificate you supplied with `use-pem` or `use-pfx` is never replaced.

Needs no elevation.

## The Windows machine store

```
dbdatasync config cert new-self-signed --dns dbdatasync.example.com
```

From an **elevated** prompt, on Windows (`--store` states it explicitly). `--dns` is required and takes one or more
names, comma-separated; the first is the subject. `--days` and `--account` are as for [enrollment](ad-cs-enroll.md).

The certificate is installed in `LocalMachine\My` with a non-exportable private key. The key is never exported, not even
briefly. Then:

```
dbdatasync config cert bind --thumbprint <thumbprint>
```

`bind` writes `Kestrel:Certificates:Default:AllowInvalid: true` for a self-signed certificate, because Kestrel refuses a
certificate whose chain it cannot build. It says so when it does.

Nothing renews a store certificate for you. `dbdatasync config cert renew`, with no CA configured, issues a fresh
self-signed one with the same subject and names. See [renewal and expiry](renewal-and-expiry.md).
