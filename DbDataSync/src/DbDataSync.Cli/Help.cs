namespace DbDataSync.Cli;

public static class Help
{
    public static void Print()
    {
        Console.WriteLine($"""
            dbdatasync — cross-database replication: a command line on a configuration repository, and the engine a Runner runs.

              dbdatasync script list | test <name> [--rows <n>] [--repo <path>] [--json]
                  The .csx scripts of the configuration, and a test of one against generated
                  sample input.

              dbdatasync provisioning plan <replication> --from-cache [--repo <path>] [--json]
                  What a replication needs of its two databases, planned offline from the
                  catalog cache. Applying it is the set's provision task on the Runner.

              dbdatasync package publish --to runner (--server <name or address> --channel <name> ... | --out <dir> | --check) [--repo <path>]
                  Publishes the replications of the repository to a Runner as a package (or
                  writes it to a folder, or only checks it). The Runner runs them.

              dbdatasync tool install|uninstall [--dir <path>]
                  Puts a `dotnet tool install --tool-path <dir>` copy of this CLI on the machine's
                  PATH — a /usr/local/bin symlink (Linux/other Unix), an /etc/paths.d entry (macOS),
                  or the Machine PATH (Windows). Needs root/an elevated prompt. Defaults --dir to
                  this executable's own directory; see docs/install.md for the full sequence.

              dbdatasync lineage graph|impact|provenance [--repo <path>]
                  The lineage of the replications' tables and columns, built offline from the
                  configuration: the graph, what a change touches, where a value comes from.

              dbdatasync library install <packageId>[ <packageId>...] [--as <id>] --version <v> [--factory-type type] [--source feed]
              dbdatasync library sync [<id>] | list | uninstall <id>
                  Restores an ADO.NET library package (Microsoft.Data.SqlClient, Npgsql,
                  MySqlConnector, ...) into <repo>/libraries/<id>/, so it can be swapped by
                  installing a new version rather than by rebuilding DbDataSync. Runs
                  `dotnet publish` under the hood — no network access from the running host
                  itself.

              dbdatasync driver install <id> --library <name> --version <v> [--factory-type type] [--from mysql] [--display-name name]
              dbdatasync driver list | uninstall <id>
                  Adds a whole new SQL engine — a driver.yaml descriptor plus its restored
                  library — without a DbDataSync rebuild. Watermark and batch-reload
                  replication only; see docs/drivers-and-libraries.md
                  for what a descriptor can and can't do.

              dbdatasync version

            The Sync server and its commands (server, service, cert, secret, config, state, health,
            gateway, update, launcher) were deleted (plan 33 SC23): the Runner runs the replications,
            and each of those commands says where its feature went.
            """);
    }
}
