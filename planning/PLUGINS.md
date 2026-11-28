# PLUGINS: each kind of project as a plugin

## Goal

Each kind of project (Sync, Build, the Store, apps and the kinds that come later) becomes a plugin. A plugin brings its parts for each role: authoring in the Workspace,
tasks on the Runner, serving on the Gateway, and its screens in the console. A plugin is a NuGet package, upgraded the way a third-party library is
upgraded. Every plugin uses one set of standard contracts for secrets, connections, deployment, agents, lineage and dependencies.

The work goes in two stages. First the plugins are chosen at build time: the program's release references chosen plugin versions and ships them as one
tested set. Later an install loads plugins at run time from a feed. The first stage follows the second stage's rules, so the second changes how plugins
are found and loaded, and leaves the plugins themselves unchanged. Breaking changes are allowed; the old programs, names and packages are not migrated.

## What exists today

- Four service programs (`dbdatakit`, `dbdataworkspace`, `dbdatarunner`, `dbdatagateway`), each its own project with its own startup code. They share
  the startup sequence, hosting, certificates, state and secrets as libraries under `Shared/src` (`DbDataKit.Hosting.*`, `DbDataKit.State.*`,
  `DbDataKit.Secrets.*`). Three command-line tools (`dbdatabuild`, `dbdatasync`, `dbdatastore`) remain; CLI removes them.
- Releases are already in lockstep: the suite release builds every product from one commit with one CalVer version (`docs/releases.md`).
- Three extension points exist, one per role, with no shared contract between them:
  - The Workspace's project kinds: `IProjectKind` (`Shared/src/DbDataKit.Authoring/ProjectKinds.cs`) gives a kind's template, check, produce step and
    publish routes. The server knows no kind by name.
  - The Runner's jobs: `RunnerJob` in `DbDataKit.Runner.Kit` gives typed inputs, connection slots and steps; the manifest is generated from the C#
    types. The task host loads each package in its own load context (`DbDataKit.Runner.TaskHost/PackageLoadContext.cs`), in its own process, and always
    takes the kit's types from the host.
  - The Gateway's modules: `IServingModule` (`DbDataKit.Serving.Abstractions`) is an endpoint enabled per instance in `server.yaml` (Flight SQL, views,
    apps, the Store, identity, MCP). The modules are compiled into the Gateway.
- The console's screens are compiled into its web app. Some kinds have their own web packages beside it (`Shared/web/sync-ui`, `Shared/web/store-ui`),
  built into the console.
- Most of the standard contracts exist as libraries: `DbDataKit.Secrets.Abstractions`, `DbDataKit.Connections.Abstractions` (`connection/1`),
  `DbDataKit.Runner.Abstractions` (`package/1`), `DbDataKit.Lineage`, `DbDataKit.Monitoring`, `DbDataKit.State`. They are versioned with the suite,
  not as an SDK a plugin builds against.

## Open designs

### The SDK: the standard contracts

**What.** A small set of contract packages that every plugin builds against, and nothing else of the suite: secrets and secret references; connections
and connection slots (`connection/1`); packages and deployment (`package/1`); the person, their roles and audit; a state module per plugin; lineage;
quality checks; monitoring and run results; agent operations (AGENTACCESS's operation); dependencies between plugins. Each contract package has a
version, listed and frozen the way `docs/formats.json` lists formats, with a repository check that fails when a contract changes without a new
version.

**Why.** A plugin that sees only the SDK can be built, versioned and later loaded at run time without the suite's internals, and a contract change is
visible to every plugin that depends on it.

**Open questions.**

- Which existing libraries become the SDK as they are, and which are split into a contract package and an implementation.
- How many versions of a contract the program supports at once (one, or the current and the previous, as formats do).
- Where the SDK's packages are published and what they are called.

### The plugin and its manifest

**What.** A plugin is a NuGet package with a manifest. The manifest names the plugin, its version, the SDK versions it works with, the plugins it
depends on, and its parts per role:

- authoring: the project kind the Workspace hosts (an `IProjectKind` today);
- runtime: the jobs a Runner runs (`RunnerJob` today), in the task host's process;
- serving: the modules a Gateway enables (`IServingModule` today);
- screens: the console's screens for the kind, including its editor.

The program finds plugins only through their manifests. At build time it reads the manifests of the packages its release references; at run time it
reads the same manifests from installed packages. A repository check fails when a plugin references anything outside the SDK.

**Why.** The same manifest works in both stages, so moving to run-time loading does not change a plugin.

**Open questions.**

- One package per plugin with every part, or one package per part (`<plugin>.Authoring`, `<plugin>.Runtime`), so a role downloads only its part.
- How a plugin's part is registered: a type named in the manifest, or a registration method the program calls with the role's services.
- How a plugin's state module and its migrations are kept apart from the program's own state and from other plugins.

### Each role loads only its own parts

**What.** A role registers only the parts of each plugin that the manifest gives for that role. The Workspace never loads a runtime or serving part,
because those parts use logins and the Workspace never handles a secret. While plugins are compiled in, every part is inside the program, so this is
enforced by registration: a test per role lists what the role exposes (routes, listeners, background services, project kinds, modules) and fails when
another role's part appears. At run time, loading a part only in its role adds a second barrier.

**Why.** The security of the suite rests on each service holding only what it needs (`docs/security.md`); a plugin that brings code for every role must
not weaken that.

**Open questions.**

- Whether a background service a plugin registers is tied to a role by the manifest or by the registration call.
- How a test proves the Workspace never reaches the secret store with a plugin installed.

### Screens that come with a plugin

**What.** The console shows each plugin's screens, including the editor every authored format needs (`docs/ui-editors.md`). The console's screens and
the kinds' web packages are built into one web app today; a plugin's screens have to be added to it, at build time first.

**Why.** A plugin that brings authoring and runtime parts and no screen leaves its formats without an editor. No mechanism for this exists today, so it
is designed before the first plugin moves.

**Open questions.**

- How a plugin ships its screens: a web package built into the console at build time, or a bundle the console loads at run time.
- What the screens can call: the console's relay and the plugin's own routes, and how the browser's API types are shared.
- How a plugin's screens get the console's layout, navigation and sign-in.

### The order of the first plugins

**What.** The app kind moves first: it is the smallest, and its parts are authoring and serving. Sync, Build and the Store follow, one at a time, each
with its authoring, runtime and screens. When the last one has moved, the four service programs and the three command-line tools are deleted.

**Why.** The app kind tests the manifest, the per-role registration and the screens with the fewest parts; Sync then adds a runtime part.

**Open questions.**

- Whether the features that are not a kind of project (delivery, quality, lineage, knowledge, agents) become plugins too, or stay in the program.

### Loading plugins at run time

**What.** An install pulls plugins from a feed and upgrades them on its own schedule, against the SDK versions the program supports. This is the second
stage and is designed after the first.

**Open questions.**

- Signing: which signatures the program accepts, and who signs a third-party plugin.
- Feeds: the list of feeds an install allows, and how an administrator changes it.
- Isolation: a load context per plugin, as the task host already gives each package, and which types are always taken from the program.
- What the console shows and does when an installed plugin needs an SDK version the program does not support.

## Needs from other subjects

- CLI: the list of commands (CLI-1), and whether one program runs every role (its open design "One program for every service, or one per
  service"); its removal of the three command-line tools and its task host in the runtime packages (CLI-4) are steps toward the runtime part of a plugin.
- AGENTACCESS: the operation (AGENTACCESS-1), the agent contract of the SDK.
- INSTALLER: the install, the service registration and the startup failure file, for one program with a role, if CLI makes one program.
- CLOUDDEV: the development instance, four processes of the one program, if CLI makes one program.

## Shared areas touched

`docs/architecture.md` (the four programs, "Command lines", "Planned architecture changes"), `Shared/src/DbDataKit.Hosting.*`,
`Shared/src/DbDataKit.Authoring`, `Shared/src/DbDataKit.Runner.Kit`, `Shared/src/DbDataKit.Runner.TaskHost`, `Shared/src/DbDataKit.Serving.Abstractions`,
the four service projects, `Shared/web/console` and the kinds' web packages, `dist/`, `docs/formats.json` (the SDK's versions), `scripts/` (the checks),
`DbDataKit.slnx`.
