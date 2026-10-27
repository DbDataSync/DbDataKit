# DbDataStore

Master data models kept in your own databases: entities, attributes, keys, references and rules written as `model/1` files, validated writes, history and audit, changes to structure by plan and approval (plans 15 and 19, lane Z).
It is a server with a management API, paired with consoles like the other products; reads and writes of the data itself are served by the serving layer (plan 20).

This is the skeleton (Z01). It starts, pairs and reports who it is; models, storage and writes arrive slice by slice (Z03 and on).

```
dbdatastore server run [--repo <server directory>] [--listen https://127.0.0.1:8490]
dbdatastore server pair --max-role <roles> [--repo <server directory>]
dbdatastore server clients | unpair <client> [--repo <server directory>]
dbdatastore service install|uninstall|status|start|stop|restart|doctor|unit
dbdatastore health check [--url <url>]
```

The server's directory is `--repo`, or `DBDATASTORE_SERVER_DIR`, or a folder under the user's local application data (`%ProgramData%\DbDataStore\server` for the Windows service). It holds `server.yaml` (the instance id and the clients paired with it),
the pairing codes, and the self-signed certificate (`tls.pfx` and its password file, readable only by the account that runs the server). A client pins the certificate's fingerprint when it pairs.

## What is served

`/health` and `/version` answer without a token. Everything under `/api` needs a token a paired console issued, within the roles the pairing allowed. So far there is `GET /api/v1/info` (the product, its version and its instance id).
