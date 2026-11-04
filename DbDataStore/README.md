# DbDataStore

Master data models kept in your own databases: entities, attributes, keys, references and rules written as `model/1` files, validated writes, history and audit, changes to structure by plan and approval (plans 15 and 19, lane Z).
It is a server with a management API, paired with consoles like the other products; reads and writes of the data itself are served by the serving layer (plan 20).

This is the skeleton (Z01). It starts, pairs and reports who it is; models, storage and writes arrive slice by slice (Z03 and on).

```
dbdatastore server run [--repo <server directory>] [--listen https://127.0.0.1:8490]
dbdatastore server pair --max-role <roles> [--repo <server directory>]
dbdatastore server clients | unpair <client> [--repo <server directory>]
dbdatastore service install|uninstall|status|start|stop|restart|doctor|unit
dbdatastore model put --file <model.yaml> [--name <model>] [--repo <server directory>]      # checked first, then saved
dbdatastore storage create --model <model> [--repo <server directory>]                      # the tables of the model's own copy, on connections/<name>.yaml
dbdatastore proxy add <name> --url <https address> --code <code> [--repo <server directory>]   # pair with a serving group as a publishing client
dbdatastore proxy list | remove <name> [--repo <server directory>]
dbdatastore model publish <model> --proxy <name> [--repo <server directory>]                 # the model, its rules and its connection, to every instance of the group
dbdatastore health check [--url <url>]
```

## Publishing to the serving layer

Reads and writes of master data are served by the serving layer (plan 20), from the models this server publishes to it (O10), so they go on with this server stopped. Pair once with the serving group as a publishing client, with a code its administrator made (`dbdataproxy server pair --max-role publisher`): `dbdatastore proxy add edge --url https://serving.example:8815 --code <code>`. `dbdatastore model publish <model> --proxy edge` then sends three things, each on the version the group has: the write login of the model's connection (kept by the group as its secret, encrypted with its key), a data source of the same name that refers to it, and the model naming that data source. Every instance of the group serves the version published and enforces its rules; one that cannot load a version keeps the one it had. Changing the structure stays here: plan it, have it approved, change the tables, then publish again. The model's approval settings go with it; permissions per entity are not in `model/1` yet.

The server's directory is `--repo`, or `DBDATASTORE_SERVER_DIR`, or a folder under the user's local application data (`%ProgramData%\DbDataStore\server` for the Windows service). It holds `server.yaml` (the instance id and the clients paired with it),
the pairing codes, and the self-signed certificate (`tls.pfx` and its password file, readable only by the account that runs the server). A client pins the certificate's fingerprint when it pairs.

## What is served

`/health` and `/version` answer without a token. Everything under `/api` needs a token a paired console issued, within the roles the pairing allowed. So far there is `GET /api/v1/info` (the product, its version and its instance id).

## In a container

`DbDataStore/Dockerfile` (build from the repository root: `docker build -f DbDataStore/Dockerfile -t dbdatastore .`) runs the server as an unprivileged user on `https://0.0.0.0:8490`, with `/data` as its directory and a health check. `dist/compose/store.yml` starts it with the console image: the server announces itself on the console's private port and waits to be confirmed (`docker compose -f dist/compose/store.yml exec console dotnet /app/dbdatakit.dll instance confirm dbdatastore`, or in the console's Servers page). `dist/compose/check.sh dist/compose/store.yml dbdatastore` does it all and checks the console lists the server as ready.

## Change requests

A write that must wait for a person is kept as a change request. An agent's write waits unless the model says `approval: {agents: direct}`; anyone's waits when the model has `approval: {changes: steward}` or `owner`. `POST /api/v1/models/<m>/changes` then answers 202 with the request; `GET /requests[?state=pending]` lists them, `GET /requests/<id>` shows one, and `POST /requests/<id>/approve` (or `/reject`, each with an optional `{"note": "..."}`) decides it. Approving applies the changes as they were proposed, through the same writer and its rules; a request they break stays pending. A steward's approval needs the author role, an owner's an administrator, and an agent (a token whose subject starts with `agent:`) never decides.
