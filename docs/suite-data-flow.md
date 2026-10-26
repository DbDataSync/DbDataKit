# How data flows through the suite (future state)

A high-level picture of the planned suite: where data enters, which product moves or shapes it, and who reads it. It is drawn from
plans 13 to 19 (`docs/plans/`). Some parts are not built yet; the labels say "later" where a plan schedules them after the first version.

## Data

```mermaid
flowchart LR
    subgraph SRC["Sources"]
        SDB[("Operational databases<br/>SQL Server, PostgreSQL, MySQL,<br/>MariaDB, Oracle, ODBC")]
        FILES[/"Documents and spreadsheets"/]
        PEOPLE(["People and agents<br/>editing master data"])
        SSAS[("SSAS models")]
    end

    subgraph STORE["DbDataStore"]
        DOCS[("Documents, versioned,<br/>stored in the database")]
        ENT[("Master data entities<br/>versioned, effective-dated")]
    end

    subgraph SYNC["DbDataSync"]
        REP["Replications"]
    end

    STAGE[("Replicated tables")]

    subgraph BUILD["DbDataBuild"]
        MODELS["Models, plans, loads"]
        SEMPKG["Semantic packages"]
    end

    WH[("Warehouse and marts<br/>SQL Server, PostgreSQL, DuckDB")]

    subgraph PROXY["DbDataProxy"]
        DS["Read-only data sources"]
        SEM["Semantic models<br/>(QIS engine)"]
    end

    subgraph USE["Consumers"]
        CLIENTS["Flight SQL clients<br/>and tools"]
        EXCEL["Excel"]
        BI["Report servers and BI tools<br/>SSRS, Power BI Report Server"]
        APPS["DbDataApps<br/>dashboards, reports, assistants"]
    end

    SDB -->|"change capture, bulk copy"| REP
    REP --> STAGE
    SDB -->|"copies, native models"| MODELS
    STAGE --> MODELS

    FILES -->|"WebDAV"| DOCS
    DOCS -->|"extraction on save"| ENT
    PEOPLE -->|"validated writes,<br/>change requests"| ENT
    ENT -->|"import, on-change trigger"| MODELS
    ENT -.->|"on-change trigger"| REP

    MODELS -->|"create and load<br/>tables and views"| WH
    MODELS --> SEMPKG
    SEMPKG -->|"deploy with the<br/>read login only"| SEM

    WH --> DS
    WH --> SEM
    SSAS --> SEM
    ENT -->|"dimensions, maps,<br/>calendars"| SEM

    DS -->|"Flight SQL"| CLIENTS
    ENT -->|"Flight SQL reads,<br/>as of a date"| CLIENTS
    SEM -->|"Flight SQL, semantic SQL,<br/>Postgres wire later"| CLIENTS
    SEM -->|"XMLA and MDX<br/>(pivot tables), later"| EXCEL
    DOCS -->|"WebDAV"| EXCEL
    SEM -->|"result feeds, ODBC,<br/>Postgres wire, later"| BI
    SEM -->|"semantic queries"| APPS
    ENT -->|"reads and writes"| APPS
```

Reading it left to right:

- **Sync** copies operational data into replicated tables.
- **DbDataStore** holds the data people own (master data and documents), with history. It extracts tables from documents as they are saved.
- **Build** turns replicated tables, sources and entities into the warehouse. It also authors the semantic packages and deploys them to
  Proxy with the read login only; the write login never leaves Build.
- **Proxy** serves the warehouse and the semantic models: Flight SQL now, with Postgres wire and XMLA for Excel later. The semantic models
  can also read SSAS and DbDataStore's entities.
- **DbDataStore** also serves its own entities over the same shared protocol code (Flight SQL, WebDAV).
- **DbDataApps, report servers, Excel and tools** read from Proxy and DbDataStore; they never reach the databases directly.

## Control

The data paths above are managed, monitored and traced from one place. A console calls the servers; a server never calls a console.

```mermaid
flowchart LR
    ID["Identity home<br/>(users, OpenID Connect)"]
    CON["dbdatakit console"]
    AG["Agents<br/>(every action approved by the user)"]

    subgraph SERVERS["Product servers"]
        S["DbDataSync"]
        B["DbDataBuild"]
        P["DbDataProxy"]
        D["DbDataStore"]
    end

    CON -->|"sign-in"| ID
    CON -->|"management API with paired tokens:<br/>configure, run, approve"| SERVERS
    SERVERS -.->|"monitoring, audit, lineage<br/>(pulled by the console)"| CON
    B -->|"pairs as a Publisher:<br/>deploys semantic packages"| P
    AG -->|"the same commands, as the user"| CON
```

- **Monitoring and lineage are pulled.** Each server keeps its own runs, audit and lineage, and the console joins them (plans 13 W10 and 18).
- **One server-to-server path.** Build pairs with Proxy as a client so it can deploy semantic packages (plan 13 W12).
