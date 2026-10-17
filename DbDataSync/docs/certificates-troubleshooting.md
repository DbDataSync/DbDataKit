# Troubleshooting certificates

**"Needs Administrator."** `enroll`, `renew`, `retrieve` and `new-self-signed` into the machine store install into
`LocalMachine\My`, which needs an elevated prompt. Nothing was changed. `list`, `status`, `templates`, `discover` and
`bind` do not need one.

**"Both a CA and a template are needed."** Pass `--ca` and `--template`, or save them. `dbdatasync config cert discover`
finds them; see [enrollment](certificates-ad-cs-enroll.md).

**`discover` or `templates` says NotDomainJoined, DirectoryUnreachable or AccessDenied.** The machine must be joined to
the domain and able to read the AD configuration partition. The message includes the underlying error. You can still
find the CA and templates by hand with `certutil`, and `--template` always accepts a name you already know.

**The CA accepted the request, but installing failed.** The message gives the request id and the command
`dbdatasync config cert retrieve --request-id <id>`. Run that rather than enrolling again.

**`enroll` says the request is pending.** The template needs a certificate manager's approval. It is not an error; run
`retrieve --request-id <id>` after it is approved. A pending request is remembered on this machine only, so retrieve it
from the same machine and configuration directory.

**The service cannot read the private key** (`status` says so). Re-run the issuing command with `--account <the service
account>`, or grant that account read on the key in the certificate store.

**The browser still shows the old certificate.** Nothing takes effect until the service restarts.

**A certificate is bound but HTTPS does not use it, and `Server` is `httpsys`.** HTTP.sys ignores the Kestrel
certificate keys; bind the certificate to the port with `netsh http add sslcert`. See [Sharing a port with
HTTP.sys](configuration.md#sharing-a-port-with-httpsys).

**An encrypted PEM key is refused.** Decrypt it first; see [PEM and PFX files](certificates-pem-and-pfx.md).
