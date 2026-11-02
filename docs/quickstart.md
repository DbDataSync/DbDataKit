# QuickStart

The whole suite on one machine in about ten minutes, with demo data to point it at. You need Docker with Compose and a checkout of this repository. Nothing here is meant for production: the demo database has a published password and port, and the servers use their own self-signed certificates.

## 1. Start it

From the repository root:

```
docker compose -f dist/compose/quickstart.yml up -d --build --wait
```

The first build takes several minutes (five images). It starts:

| Service | What it is | Reachable at |
| --- | --- | --- |
| `console` | the web app that manages the others | https://localhost:8480 |
| `proxy` | DbDataProxy, the Arrow Flight SQL proxy | `localhost:8815` (Flight), management inside the network |
| `sync` | DbDataSync, replication | through the console |
| `build` | DbDataBuild, tables and views from SQL models | through the console |
| `store` | DbDataStore, master data models | through the console |
| `demo-db` | PostgreSQL 17 with demo data | `localhost:15434`, user `demo`, password `demo` |

The demo database `demo` holds `customers` (4 rows) and `orders` (5 rows). `demo_target` is empty, for Sync to replicate into; `mdm` is the Store's.

## 2. Sign in

The console has no user yet, so it makes a one-time invitation for the first administrator:

```
docker compose -f dist/compose/quickstart.yml exec console cat /data/FIRST-RUN.txt
```

Open the `https://localhost:8480/invite#<code>` address in it and register a passkey. Your browser warns about the certificate (it is self-signed); accept it for `localhost`. If the invitation has expired, make a fresh one:

```
docker compose -f dist/compose/quickstart.yml exec console dotnet /app/dbdatakit.dll user invite --role admin --email you@example.com --url https://localhost:8480
```

## 3. Confirm the servers

Each server announces itself to the console and waits. In the web app open **Servers**: the waiting ones are listed above the paired ones, each with a Confirm button. Confirm all four (`dbdataproxy`, `dbdatasync`, `dbdatabuild`, `dbdatastore`). The same from the command line:

```
docker compose -f dist/compose/quickstart.yml exec console dotnet /app/dbdatakit.dll instance pending
docker compose -f dist/compose/quickstart.yml exec console dotnet /app/dbdatakit.dll instance confirm dbdatasync
```

A confirmed server shows as ready, with its version. Sync's screens open from its entry in the list.

## 4. Try something

Use the demo database from any of the products. From inside the network it is host `demo-db`, port `5432`, user `demo`, password `demo`, database `demo`; from your machine it is `localhost:15434`.

* **Look at the data** with any PostgreSQL client: `psql "host=localhost port=15434 user=demo password=demo dbname=demo" -c "select * from orders"`.
* **Replicate** `demo` into `demo_target` with Sync: add the two connections and a replication on Sync's screens.
* **Serve it** through the Proxy: add `demo` as a data source and connect an Arrow Flight SQL client to `localhost:8815`.
* **Model it** with Build: a Build project of SQL models over `demo`, planned and applied from the console.
* **Master data** with the Store: it already has a connection `mdm` (`connections/mdm.yaml` in its directory, logging in through the `MDM_CONNECTION` environment variable of the compose file). Save a `model/1` file whose `storage: {connection: mdm}`, create its tables and write rows over the Store's management API through the console's relay, `/relay/<server>/api/v1/models/...`: `PUT /models/<name>`, `POST /models/<name>/storage/create`, `POST /models/<name>/changes`, `GET /models/<name>/entities/<entity>/rows`. `DbDataStore/README.md` has the details.

## 5. Stop it

```
docker compose -f dist/compose/quickstart.yml down        # keeps the data
docker compose -f dist/compose/quickstart.yml down -v     # starts from nothing next time
```

## If something does not work

* `docker compose -f dist/compose/quickstart.yml ps` shows which service is not healthy; `logs <service>` says why.
* A server missing from the waiting list: it announces every half minute, so wait a little, then check its log.
* Ports 8480, 8815 and 15434 must be free on the machine. Change the left-hand number of the `ports:` entry in `dist/compose/quickstart.yml` to move one.
* `dist/compose/check.sh dist/compose/quickstart.yml dbdataproxy dbdatasync dbdatabuild dbdatastore` starts a fresh copy, confirms every server and checks the console lists them as ready, then removes it; CI runs the same.
