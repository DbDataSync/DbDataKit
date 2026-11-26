# QuickStart

The console and a Gateway on one machine in about ten minutes, with demo data to point them at. You need Docker with Compose and a checkout of this repository. Nothing here is meant for production: the demo database has a published password and port, and the servers use their own self-signed certificates.

The suite is four services: the console, the Workspace (where projects are authored), the Gateway (which serves data) and the Runner (which runs the published work). This quickstart has the two that have a container image, the console and the Gateway; the Workspace and the Runner are installed from their packages (`docs/suite-single-server-install.md` installs all four on one machine).

## 1. Start it

From the repository root:

```
docker compose -f dist/compose/quickstart.yml up -d --build --wait
```

The first build takes several minutes (two images). It starts:

| Service | What it is | Reachable at |
| --- | --- | --- |
| `console` | the web app that manages the others | https://localhost:7800 |
| `gateway` | DbDataGateway, the Arrow Flight SQL gateway | `localhost:7821` (Flight), management inside the network |
| `demo-db` | PostgreSQL 17 with demo data | `localhost:15434`, user `demo`, password `demo` |

The demo database `demo` holds `customers` (4 rows) and `orders` (5 rows). `demo_target` is empty, for a replication to write into; `mdm` is for a master data model.

## 2. Sign in

The console has no user yet, so the first administrator comes in through a **setup link**: an address that works once and ends after 24 hours. Make it on the machine, with the address you will open the console at:

```
docker compose -f dist/compose/quickstart.yml exec console dotnet /app/dbdatakit.dll setup link --url https://localhost:7800
```

Open the link it prints (`https://localhost:7800/invite#<code>`) and register a passkey: you are the first administrator. Your browser warns about the certificate (it is self-signed); accept it for `localhost`. If the link has ended or is lost, run the same command again: it makes a new one, and the old one stops working. Once someone has signed up there is no setup link to make; to let another person in, make an invitation:

```
docker compose -f dist/compose/quickstart.yml exec console dotnet /app/dbdatakit.dll user invite --role admin --email you@example.com --url https://localhost:7800
```

After you sign in, **Setup** (under **Settings** in the navigation on the left) lists the first steps (pair the servers, add a connection, make a first project in the Workspace) and finishes each one by what the console actually has.

## 3. Confirm the servers

Each server announces itself to the console and waits. In the web app open **Servers**: the waiting ones are listed above the paired ones, each with a Confirm button. Confirm each of them (`dbdatagateway`). The same from the command line:

```
docker compose -f dist/compose/quickstart.yml exec console dotnet /app/dbdatakit.dll instance pending
docker compose -f dist/compose/quickstart.yml exec console dotnet /app/dbdatakit.dll instance confirm dbdatagateway
```

A confirmed server shows as ready, with its version, and its screens open from its entry in the list.

## 4. Try something

Use the demo database from any of the products. From inside the network it is host `demo-db`, port `5432`, user `demo`, password `demo`, database `demo`; from your machine it is `localhost:15434`.

* **Look at the data** with any PostgreSQL client: `psql "host=localhost port=15434 user=demo password=demo dbname=demo" -c "select * from orders"`.
* **Serve it** through the Gateway: add `demo` as a data source (the Gateway's **Data sources** screen, from **Servers**) and connect an Arrow Flight SQL client to `localhost:7821`.
* **Replicate, model or keep master data**: a replication (a sync project), SQL models (a build project) or a master data model (a store project) is authored in the Workspace and published to a Runner, which runs it; a model's rows are written through a Gateway. Neither the Workspace nor the Runner is in this quickstart: add them from their packages, pair them with this console, and point them at `demo-db` (from your machine, `localhost:15434`).

## 5. Stop it

```
docker compose -f dist/compose/quickstart.yml down        # keeps the data
docker compose -f dist/compose/quickstart.yml down -v     # starts from nothing next time
```

## If something does not work

* `docker compose -f dist/compose/quickstart.yml ps` shows which service is not healthy; `logs <service>` says why.
* A server missing from the waiting list: it announces every half minute, so wait a little, then check its log.
* Ports 7800, 7821 and 15434 must be free on the machine. Change the left-hand number of the `ports:` entry in `dist/compose/quickstart.yml` to move one.
* `dist/compose/check.sh dist/compose/quickstart.yml dbdatagateway` starts a fresh copy, confirms every server and checks the console lists them as ready, then removes it; CI runs the same.
