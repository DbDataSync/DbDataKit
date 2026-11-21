# DbDataKit.Outbound

The outbound list of a service: the destinations its own code may connect to, and the one door every outbound connection goes through.

```yaml
outbound:
  - https://api.nuget.org
  - https://*.githubusercontent.com
  - ssh://github.com
  - tcp://db.internal:5432
```

- **One list, one setting.** `outbound:` in the service's settings file (`settings.yaml` of the console, `server.yaml` of the others) is a list of `scheme://host[:port]`. The scheme gives the port its default (`http` 80, `https` 443, `ssh` 22, `git` 9418); `tcp` has none, so it needs a port. A host is a name, an address, or `*.suffix` for every name under the suffix (not the suffix itself). A path, a query or a login in an entry is refused.
- **No setting, no limit.** A settings file without the line connects anywhere. `outbound: []` connects nowhere. A line with no value fails, so an emptied list is never read as either.
- **Strict.** A value that is not a list, or an entry that is not valid, fails at start naming the file, the line and the entry.

```csharp
var list = OutboundSettings.Load(Path.Combine(dataFolder, "server.yaml"));
services.AddOutboundGate(list, "workspace");                                   // IOutboundAudit, when the host registered one, receives the refusals
services.AddHttpClient("packages").AddOutboundGate("package.fetch");           // every connection of the client, redirects included, is checked first

gate.Check("git.fetch", "git@github.com:org/repo.git");                        // CheckGitRemote: a folder on this machine passes
gate.Check("runner.call", new Uri("https://runner.internal:7840/api/v1/runs"));
```

- **Refused before connecting.** `OutboundRefusedException` names the service, the operation and the destination. The same facts are logged as a warning and passed to `IOutboundAudit.Refused` as an `OutboundRefusal` (time, service, operation, host, port); a failing audit does not hide the refusal.
- **Only host and port.** The log and the audit never hold a path, a query, a login or a header of the request.
- **What it sees.** The handler checks each connection the client opens, so a redirect to an address that is not listed is refused. Behind an HTTP proxy the connection goes to the proxy, so the proxy's address is the destination that is checked. A program the service starts (git, `dotnet restore`) opens connections of its own: the caller checks that operation's destinations with `Check` before it starts it, and only the operating system stops the program itself.
