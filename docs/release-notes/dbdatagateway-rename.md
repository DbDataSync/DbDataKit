# DbDataProxy is now DbDataGateway

Release note for plan 20, O14. A clean break: the new program refuses the old names with a message that names the old name, the new one and the file or variable it found. It does not read the old names and does not accept both.

| Was | Is |
| --- | --- |
| product `DbDataProxy` | `DbDataGateway` |
| command and program `dbdataproxy` (`dbdataproxy.exe`, the worker `dbdataproxy-worker.exe`) | `dbdatagateway` (`dbdatagateway.exe`, `dbdatagateway-worker.exe`) |
| Windows service and systemd unit `dbdataproxy` | `dbdatagateway` |
| data directory `%ProgramData%\DbDataProxy` | `%ProgramData%\DbDataGateway` |
| settings section `Proxy`, environment prefix `DBPROXY_` (`DBPROXY_Proxy__Listen__Port`), `--Proxy:Listen:Port=...` | section `Gateway`, prefix `DBGATEWAY_` (`DBGATEWAY_Gateway__Listen__Port`), `--Gateway:Listen:Port=...` |
| `config/proxy/auth.yaml`, `config/proxy/users.yaml` | `config/gateway/auth.yaml`, `config/gateway/users.yaml` |
| `extensions: proxy:` in a connection file | `extensions: gateway:` |
| secret names `dbdata:proxy:*`, references `secret:dbdata:proxy:*` | `dbdata:gateway:*`, `secret:dbdata:gateway:*` |
| `DBPROXY_PASSWORD`, `DBDATAPROXY_CLIENT_DIR` | `DBGATEWAY_PASSWORD`, `DBDATAGATEWAY_CLIENT_DIR` |
| .NET projects and namespaces `DbDataProxy.*`, packages `DbDataProxy.*` | `DbDataGateway.*` |
| release tags `dbdataproxy/v<version>`, assets `dbdataproxy-v<version>-win-x64.msi` | `dbdatagateway/v<version>`, `dbdatagateway-v<version>-win-x64.msi` |
| web package `@dbdatakit/proxy` | `@dbdatakit/gateway` |
| console and Update product id `dbdataproxy` | `dbdatagateway` |

## Moving an installation

1. Stop the old service (`dbdataproxy service stop`) and remove it (`dbdataproxy service uninstall`). `dbdatagateway service install` refuses while the old service is registered.
2. Install DbDataGateway. The old program does not update itself to it: its update check follows `dbdataproxy/v` releases, so install the new MSI or zip by hand once.
3. Move the data directory to `%ProgramData%\DbDataGateway` (or point the new program at the old folder with `--Gateway:DataDirectory=<folder>`).
4. Rename what the program names when it stops: the environment variables, `config/proxy` to `config/gateway`, `extensions: proxy:` to `extensions: gateway:` in connection files, and the secrets. For a secret: `dbdatagateway secret set dbdata:gateway:<rest>` with the same value, then `dbdatagateway secret delete dbdata:proxy:<rest>`; change `secret:dbdata:proxy:` references in the connection files.
5. A console that paired the old server follows the server's own answer, so it shows DbDataGateway once the server is upgraded. A server still announcing itself as `dbdataproxy` is refused with a message.

## Not renamed (plan 20, O14b)

Build's `server proxy` commands, its `/api/v1/proxies` calls and `build/proxies.json`, and Store's `proxy` commands and `--proxy` flag keep their names for now: they are those products' words for a server they publish to, and a rename there is a clean break of their own.
