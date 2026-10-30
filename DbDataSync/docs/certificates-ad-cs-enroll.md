# Enrolling from an Active Directory CA

Windows only, on a machine joined to the domain. `enroll` asks your enterprise CA (AD CS) for a certificate and
installs it into the machine store.

You need three things: the **CA**, a **template**, and the DNS names the certificate must cover.

## Find the CA and template

```
dbdatasync cert discover
```

Reads Active Directory (no elevation needed) and does the obvious thing:

- **One CA is published:** it saves it as `DbDataSync:Certificates:CaConfig` and goes on to its templates.
- **Several CAs:** it saves nothing. It lists each one with the command that saves it, for example
  `dbdatasync config set DbDataSync:Certificates:CaConfig "ca01.corp.local\Corp Issuing CA 1"`. Run that for the one
  you want, then run `discover` again.
- **One template is published on the CA:** it saves that as `DbDataSync:Certificates:Template`.
- **Several templates:** it lists them, each with the command that saves it.

A value that is already set is never overwritten. When both are set it prints the `enroll` command to run next.

Saving a value is an ordinary config write: it lands in `server.yaml` and is committed like every other
config change.

### What discover does not decide

CAs usually publish many templates, and `discover` lists their names without judging them, so on most CAs you pick the
template yourself. A template suits a web console when it issues a **server authentication** certificate, lets the
requester supply the subject and DNS names, and lets the account running `enroll` enroll. Ask whoever runs the CA if the
names do not make it obvious. Use the template's own name, not its display name.

### Finding them by hand

If `discover` cannot reach Active Directory it says why (not domain-joined, directory unreachable, access denied).
From a domain-joined machine:

- **CA:** run `certutil` with no arguments. The `Config:` line is the string to use, in the form `CASERVER\CA Name`. The CA
  console (`certsrv.msc`) shows the same name. Or ask the PKI administrator.
- **Templates:** `dbdatasync cert templates --ca "CASERVER\CA Name"` lists those the CA publishes, and
  `certutil -CATemplates -config "CASERVER\CA Name"` is an alternative.

Save them with `dbdatasync config set DbDataSync:Certificates:CaConfig "..."` and `... DbDataSync:Certificates:Template
"..."`, or pass `--ca` and `--template` to `enroll` each time.

## Enroll

From an **elevated** prompt (it installs into `LocalMachine\My`):

```
dbdatasync cert enroll --dns dbdatasync.corp.local
```

`--dns` takes one or more names, comma-separated; the first is the subject. `--ca` and `--template` override the
saved values. `--account` names who may read the private key, defaulting to the account the installed `DbDataSync`
service runs as.

Then bind it and restart:

```
dbdatasync cert list
dbdatasync cert bind --thumbprint <thumbprint>
```

## When the CA needs approval

If the template requires a certificate manager to approve, `enroll` reports a **request id** and exits. That is not an
error. When it is approved, from an elevated prompt:

```
dbdatasync cert retrieve --request-id <id>
```

## If installing fails after the CA issued it

The CA has already issued the certificate, so the request id is saved before anything is installed. If installing fails,
the message names the exact command that finishes the job:

```
dbdatasync cert retrieve --request-id <id>
```

Do not run `enroll` again: it asks the CA for a second certificate.

## Renewing

`dbdatasync cert renew` re-enrolls using the bound certificate's subject and names. It is a fresh request, not a
signed renewal, so it works even after the certificate has expired. See [renewal and expiry](certificates-renewal-and-expiry.md).
