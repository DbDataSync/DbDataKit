# How data flows through the suite (future state)

A high-level picture of the planned suite: where data enters, which product moves or shapes it, and who reads it. It is drawn from
plans 13 to 20, with plan 20's single serving layer as the only edge end users reach. Some parts are not built yet; the
labels say "later" where a plan schedules them after the first version.

## Data

```mermaid
flowchart LR
    subgraph SRC["Sources"]
        SDB[("Operational databases<br/>SQL Server, PostgreSQL, MySQL,<br/>MariaDB, Oracle, ODBC")]
        SSAS[("SSAS models")]
    end

    subgraph BACK["Back-end servers (private)"]
        REP["DbDataSync<br/>replications"]
        MODELS["DbDataBuild<br/>models, loads, semantic packages"]
        STOREDEF["Store models (a Workspace store project)<br/>models, rules, structure; deployed by a Runner"]
    end

    subgraph MANAGED["Managed databases (targets the suite writes)"]
        STAGE[("Replicated tables")]
        WH[("Warehouse and marts<br/>SQL Server, PostgreSQL, DuckDB")]
        ENT[("DbDataStore database<br/>entities with history, documents")]
    end

    subgraph EDGE["Serving layer (DbDataGateway code base), one or more instances"]
        DS["flightsql<br/>read-only data sources"]
        SEM["semantic<br/>semantic models, joins across sources"]
        STOREMOD["store and webdav<br/>entity reads and writes, documents"]
        APPSMOD["apps and mcp<br/>DbDataApps, agents"]
        IDMOD["identity<br/>sign-in, OpenID Connect"]
        LATER["pgwire, xmla, feeds<br/>later"]
    end

    subgraph USE["End users and their tools"]
        CLIENTS["Flight SQL clients and tools"]
        EXCEL["Excel"]
        BI["Report servers and BI tools"]
        PEOPLE(["People, apps and agents"])
    end

    SDB -->|"change capture, bulk copy"| REP
    REP --> STAGE
    SDB -->|"copies, native models"| MODELS
    STAGE --> MODELS
    ENT -->|"import, on-change trigger"| MODELS
    MODELS -->|"create and load"| WH
    STOREDEF -->|"structure changes"| ENT

    MODELS -.->|"publish semantic packages<br/>(no login)"| SEM
    STOREDEF -.->|"publish models, rules,<br/>permissions"| STOREMOD

    WH --> DS
    WH --> SEM
    SSAS --> SEM
    ENT --> SEM
    ENT <-->|"reads, validated writes,<br/>history, audit"| STOREMOD

    DS --> CLIENTS
    SEM --> CLIENTS
    SEM --> APPSMOD
    STOREMOD --> APPSMOD
    STOREMOD -->|"WebDAV"| EXCEL
    LATER --> EXCEL
    LATER --> BI
    APPSMOD --> PEOPLE
    IDMOD --> PEOPLE
```

Reading it left to right:

- **Sync** copies operational data into replicated tables (a sync project of the Workspace, run by the Runner).
- **Build** turns replicated tables, sources and DbDataStore's entities into the warehouse (a build project of the Workspace, refreshed by
  the Runner). The Workspace publishes its semantic models and the read-only data source documents to the serving layer, with no login:
  the read login is the Gateway's, the write login is bound on the Runner.
- **DbDataStore** models are authored in a store project of the Workspace; the Runner deploys their structure and the Workspace
  publishes them to the serving layer.
- **The serving layer** is the only thing end users reach. It reads the warehouse, SSAS and DbDataStore's database directly, and it
  writes DbDataStore's data through DbDataStore's own write path (validation, history, audit). It also hosts sign-in, apps and the
  agents' endpoint.
- Because it runs from published definitions, it keeps serving while the back-end servers are patched (plan 20, section 5). Dotted lines
  are publishes; solid lines are data.

## Control

The data paths above are managed, monitored and traced from one place. A console calls the servers; a server never calls a console.

```mermaid
flowchart LR
    CON["dbdatakit console<br/>(private)"]
    AG["Agents<br/>(every action approved by the user)"]

    subgraph EDGE["Serving group"]
        ID["identity module<br/>(the identity home)"]
        SV["serving instances"]
    end

    subgraph SERVERS["Back-end servers"]
        S["DbDataSync"]
        B["DbDataBuild"]
        D["Store models (Workspace, Runner)"]
    end

    CON -->|"sign-in (OpenID Connect client)"| ID
    CON -->|"management API with paired tokens:<br/>configure, run, approve"| SERVERS
    CON -->|"management API"| EDGE
    SERVERS -.->|"monitoring, audit, lineage<br/>(pulled by the console)"| CON
    EDGE -.->|"monitoring, run lineage"| CON
    B -->|"pairs as a Publisher"| EDGE
    D -->|"pairs as a Publisher"| EDGE
    AG -->|"mcp module, as the user"| SV
```

- **Monitoring and lineage are pulled.** Each server keeps its own runs, audit and lineage, and the console joins them (plans 13 W10 and 18).
- **Publishers.** The console deploys to the serving group and to the Runner as the signed-in person and signs each version with its own publisher key, which each group trusts; a Runner pairs with the serving group to tell its cache that data changed.
- **Identity faces users.** The identity home is a module of the serving group, so end users sign in without reaching the console. It can
  also run on its own behind the firewall, with the serving layer forwarding its endpoints.
