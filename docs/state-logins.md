# One login per service and group
<!-- reviewed: 2026-11-21 -->

Services that keep their state on one SQL Server or PostgreSQL server each connect with a login of their own. A login is granted the schemas of its service and group and nothing else, so the Gateway's login is refused on the Runner's tables and the Runner's on the Gateway's.

## The login's name

The login is `dbdatakit_<service>` or `dbdatakit_<service>_<group>`. The service is the program's name without `dbdata`.

| Service and group | Login |
| --- | --- |
| the console | `dbdatakit_console` |
| a Gateway group `sales` | `dbdatakit_gateway_sales` |
| a Runner group `main` | `dbdatakit_runner_main` |
| the Workspace server | `dbdatakit_workspace_main` |

Every instance of a group uses its group's login. A second group is a second login.

## One database, one set of schemas per login

`state init --topology install` writes the `state:` section of the service's `server.yaml` with namespaces that start with the login's name:

```yaml
state:
  engine: postgres
  connection: "Host=db;Database=dbdatakit;Username=dbdatakit_runner_main;Password=secret:state-db"
  namespaces:
    default: { schema: "dbdatakit_runner_main_{module}" }
  registry: { schema: "dbdatakit_runner_main_registry" }
```

Each module the Runner keeps, including the modules every service has (runs, audit, payloads), is a schema named `dbdatakit_runner_main_<module>`. The Gateway's copy of the same module is `dbdatakit_gateway_main_<module>`. Each login has its own registry of modules, so two services never share a registry row.

Settings that already name a schema, a prefix, a file or a registry are kept as they are.

## Granting

By default an administrator creates the login (its password is never printed or made by the suite), then runs the statements `state grants` prints:

```
$ dbdatarunner state grants
-- login dbdatakit_runner_main: the one login of the runner, group main; it is granted these schemas and no others
GRANT USAGE ON SCHEMA dbdatakit_runner_main_runs TO dbdatakit_runner_main;
...
```

- `--login <name>` grants another login, for example one a company's rules name.
- `--group <name>` prints the login of another group of the same service.
- `--migrate` grants the rights to create and alter tables, for the login that upgrades the database; the login a service runs as needs no more than the default.

`state init --topology install --login <name> --admin-connection <reference> --apply` creates the schemas and runs the same statements through the administrator's connection.

## Limits

- By default the login is created by the administrator. A login that is not the default name works the same way: the schemas still start with the default name.
- The topology `group` (one database for one group) writes the same per-login names as `install` (`dbdatakit_<service>_<group>_<module>` and its registry schema), and the login is granted those schemas. A group setup made earlier on the built-in names (`dbdatakit_<module>`) is not migrated: the service refuses to start and the message names the settings file and the `state:` setting.
- A service whose settings already hold data in other schemas keeps them: `state init` refuses to move them and points to `state move`.
- The console shows another service's runs, audit and notifications through that service's API. This works wherever the console can reach the service, including a service whose database the console has no network path to (SQL Server Express on its machine, a database in a private container network). The console's login reads only its own modules, and no login reads another's schemas.
