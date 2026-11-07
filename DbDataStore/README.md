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
dbdatastore model publish <model> [--proxy <name>] [--dry-run] [--repo <server directory>]   # make the model as it is now available: its tables, then every group it is published to
dbdatastore model unpublish <model> --proxy <name> [--repo <server directory>]
dbdatastore health check [--url <url>]
```

## Publishing to the serving layer

Reads and writes of master data are served by the serving layer (plan 20), from the models this server publishes to it, so they go on with this server stopped. Pair once with the serving group as a publishing client, with a code its administrator made (`dbdataproxy server pair --max-role publisher`): `dbdatastore proxy add edge --url https://serving.example:8815 --code <code>`.

**Publishing is the one act that makes the model you are working on available** (plan 20 O10b). Editing a model's file changes nothing for anyone; `dbdatastore model publish <model>` (the first time with `--proxy <name>`; after that, to every group the model is published to):

1. brings the model's tables to it: creates them when there are none, otherwise only adds to them (a change that needs more, such as a narrower type, is refused and nothing is published);
2. pins this server's copy to that version;
3. sends that version to each group, with the write login of the model's connection (kept by the group as its secret, encrypted with its key) and a data source of the same name that refers to it.

A change to rules only (a new `pattern`, a new `max`) is published the same way, with nothing to run on the tables. `--dry-run` lists the statements and the groups and changes nothing. A group that does not take the version (it cannot be reached) keeps the one it had, and the answer says so: the tables and this server's copy are at the new version, and publishing again sends it. Publishing again with no edit sends the same version, which a group takes as unchanged.

**A published model is read and written in its groups only.** This server refuses its rows, changes and change requests (409, naming the groups), and applying a migration or creating its tables here, so one definition is ever enforced on its tables; a dry-run migration still answers. `dbdatastore model unpublish <model> --proxy <name>` takes it off a group; off every one, this server reads and writes it again. A model with instances cannot be published yet (a group serves a model's own copy only), and its approval settings go with it; permissions per entity are not in `model/1` yet.

## What is served

`/health` and `/version` answer without a token. Everything under `/api` needs a token a paired console issued, within the roles the pairing allowed. So far there is `GET /api/v1/info` (the product, its version and its instance id).

## In a container

`DbDataStore/Dockerfile` (build from the repository root: `docker build -f DbDataStore/Dockerfile -t dbdatastore .`) runs the server as an unprivileged user on `https://0.0.0.0:8490`, with `/data` as its directory and a health check. `dist/compose/store.yml` starts it with the console image: the server announces itself on the console's private port and waits to be confirmed (`docker compose -f dist/compose/store.yml exec console dotnet /app/dbdatakit.dll instance confirm dbdatastore`, or in the console's Servers page). `dist/compose/check.sh dist/compose/store.yml dbdatastore` does it all and checks the console lists the server as ready.

## Change requests

A write that must wait for a person is kept as a change request. An agent's write waits unless the model says `approval: {agents: direct}`; anyone's waits when the model has `approval: {changes: steward}` or `owner`. `POST /api/v1/models/<m>/changes` then answers 202 with the request; `GET /requests[?state=pending]` lists them, `GET /requests/<id>` shows one, and `POST /requests/<id>/approve` (or `/reject`, each with an optional `{"note": "..."}`) decides it. Approving applies the changes as they were proposed, through the same writer and its rules; a request they break stays pending. A steward's approval needs the author role, an owner's an administrator, and an agent (a token whose subject starts with `agent:`) never decides.
