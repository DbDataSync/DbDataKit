# shared/

Shared DbDataKit.* libraries (see `../docs/plans/`). One folder per package under `src/`, tests under `tests/`.
Rules: net10.0, nullable, warnings as errors, central package versions (`Directory.Packages.props`),
no dependency on any product repo. Each package has its own README documenting its public contract.
Build/test one package: `dotnet test shared/tests/<Package>.Tests`.
