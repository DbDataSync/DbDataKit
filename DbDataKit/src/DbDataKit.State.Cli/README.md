# DbDataKit.State.Cli

The shared `state` noun of every product's command line (plan 29b, M7). A product lists `StateVerbs.Create(new StateVerbContext(...))` with its other nouns and passes its program name, how to find its state from the command's options (`Locate`), its module list (or `null` when it has none yet) and its scope.

* `state show [--offline]`: engine, connection (secrets masked, references shown as written), where each module's tables are, each module's version in the registry against this binary, what is pending. Read-only: it opens no `StateDatabase`, so nothing is created or migrated; a missing SQLite file stays missing.
* `state test`: settings, connection, registry, rights (`StateRights`), engine version (`EngineSupport`), schema clash and refusals, pending migrations the login may not apply. Stops at the first failure with one sentence that names the fix. Exit codes (`StateExit`): 0 ok, 2 wrong use, 10 settings, 11 unreachable or no such database, 12 login refused, 13 registry unreadable, 14 rights missing, 15 engine too old, 16 schema clash, 17 pending migrations this login may not apply, 18 the service would refuse the database.
* `state grants --login <name> [--migrate]`: the `GRANT` statements of `StateGrants` for the product's layout.

Verbs added later (the snapshot verbs) go in files of their own and are added to the list in `StateVerbs.Create`; the three above do not change.
