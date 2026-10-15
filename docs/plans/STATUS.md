# Plan status

## Config (Plan 01)

- Done (shared/src, tests green via `dotnet test --project shared/tests/<Pkg>.Tests`): DbData.Config.Yaml (reader, merge, editor, diff, fuzz), DbData.Config (attributes, binder, layering, env/CLI, migrations, JSON Schema), DbData.Config.Hosting (MEC provider, options validation, ShowOrigin).
- Not done: DbData.Config.Testing; products do not consume the packages yet (phases 1, 5, 6); JSON input format behind the node tree; git-commit backup mode; `schema: kind/int` form; CLI commands (`config show/migrate/schema`) are library APIs only.
