# DbDataKit.Update.Detect

Install-method detection for CLIs and the matching appliers.

## Public contract
- `InstallEnvironment(ProcessPath, BaseDirectory, Env, HasDockerEnvFile)` (`FromCurrent()` captures the real machine) and `InstallMethodDetector.Detect(env)` -> `InstallDetection(Method, Evidence)`. Pure and separator/case agnostic.
- Order: Container (`DOTNET_RUNNING_IN_CONTAINER=true`, `KUBERNETES_SERVICE_HOST`, `/.dockerenv`) > Scoop (`scoop\apps|shims`, `SCOOP`/`SCOOP_GLOBAL` roots) > WinGet > dotnet tool (`~/.dotnet/tools`, `.store`, `DOTNET_TOOLS_PATH`) > ServiceSwap (`versions\<semver>\`) > Archive > Unknown.
- Appliers (`IUpdateApplier.Plan` returns a message and optionally a `CommandLine`; nothing is executed): `PackageManagerHint` (scoop/winget), `DotnetToolApplier` (builds `dotnet tool update -g <id> --version <v>` as an argument list), `ManualApplier`, `ContainerNotice`, `ServiceSwapNotice`. `UpdateApplierSelector` picks by method.

## Decisions / threats
- Containers win over path hints so in-place update prompts never appear in Docker/K8s.
- Tool ids and image refs are validated by allow-list regex; commands are argument lists, never shell strings (no injection via a manifest-supplied id).
- Executing a built command requires explicit user consent in the host; this library never runs processes.
