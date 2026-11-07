# Working in DbDataRunner

The runner host (`dbdatarunner`): plan 28 and its slices in `docs/plans/28a-runner-slices.md`. It is a server on plan 13's shape (the same shape as DbDataStore): HTTPS with the shared pairing and token check, a management API, a state
database, `server`, `service`, `cert`, `state` and `health` nouns from the shared libraries. What it adds is the runner itself. **The engine is a library**, `DbDataKit.Runner` (artifact cache, executor, scheduler loop), because the console hosts a copy
of it too (plan 28 section 6a): keep the engine there and only the host here. Do not copy shared code into this product.

| File | What |
| --- | --- |
| `RunnerCli.cs` | the command line: the router, `server run`, the local verbs (`runs list`, `tasks ...`, `package install`) |
| `RunnerServer.cs` | the web host: Kestrel, pairing, the token check, the management API, the engine as a hosted service |
| `RunnerApi.cs` | the management API (`/api/v1/status`, `/runs`, `/tasks/.../run|pause|resume`) and its roles |
| `RunnerEngine.cs` | the hosted service: task host, protocol socket, executor, scheduler, instance registration and heartbeat, graceful stop |
| `RunnerGroupApi.cs`, `RunnerLabelVerbs.cs` | the group view (`/api/v1/instances`) and `label add|remove|list` |
| `RunnerControl.cs` | run now, pause, resume and their audit, shared by the API and the command line |
| `RunnerPackages.cs` | the `integration` kind (its validator, slots from the manifest) and `package install`, the development path over `RunnerPublishing` |
| `RunnerPublishing.cs` | the publish service layer (artifact upload, publish, activate, switch, rollback, retire, bind, reads, the projection rebuild, the audit); the API and `package install` both call it |
| `RunnerPublishApi.cs` | the publish API (`/api/v1/artifacts`, `/definitions/integration`, `/bindings`, `/settings`) and who may call what |
| `RunnerPublishClient.cs` | the client side: `package pair` and `package publish` (stored pairings, HEAD then upload, then publish) |
| `RunnerContracts.cs`, `RunnerSignatures.cs` | the .NET, kit and protocol contracts a publish is checked against; the signature seam (`IVersionSignatureVerifier`, not built) |
| `RunnerState.cs`, `RunnerSettings.cs` | the modules the state is built from and how it is opened; the directory, the listen address, the channels |
| `RunnerTaskHost.cs` | finds the task host beside the program |
| `RunnerService.cs` | the `service` and `cert` nouns |

## Rules

- **Publishing.** A publisher may only change the packages it created (`OwnerScope`; the owner is kept in the group's settings); activation, switch, rollback, retire and `require_signatures` are an administrator's; every change and every refusal is audited, never with file content. Whatever changes what a channel serves rebuilds that package's task projection in `RunnerPublishing`, never elsewhere.
- **Roles.** Reading is for a viewer, starting, pausing and resuming for an operator (an administrator is one). Every act that changes a task goes through `RunnerControl`, so it is audited the same way from the API and the command line; a refused one is audited too. Audit details hold the names of inputs, never their values.
- **The task host** is published into `taskhost/` beside the program (the server project's targets do it on build and publish) because it has its own dependencies and runtime configuration. A test (`The_task_host_is_found_beside_the_server_in_its_build_output`) proves the build did it; the server does not start without it.
- **The state** is one database for the modules in `RunnerState.Modules`. SQLite in the directory by default; `DBDATARUNNER_STATE_ENGINE` and `DBDATARUNNER_STATE_CONNECTION` name a server engine (until plan 29b C07 makes the shared `state` section the one way). SQLite is for a single runner only; a group shares a server engine. The runs a dead process left are marked interrupted at start only when no other instance of the group is live.
- **`--help` is a snapshot.** `tests/DbDataRunner.Tests/help.snapshot.txt` is the help of every noun; a change to a verb's text changes it (`DBDATA_UPDATE_SNAPSHOTS=1 DBDATA_SNAPSHOT_FILE=<path to the file>` rewrites it). `scripts/check_docs_commands.py` reads the nouns and verbs from the sources and holds `README.md` to them: a new noun is in the README.
- **A clean break is allowed, never a silent one**: a changed file format fails with a message naming the file.

## Build and test

From this folder: `dotnet build`, then `dotnet test --project tests/DbDataRunner.Tests` (xunit v3, MTP; if the test host cannot start, run the built executable: `tests/DbDataRunner.Tests/bin/Debug/net10.0/DbDataRunner.Tests`; `-class` and `-method` select tests).

- The tests start real servers and real task processes. They run on SQLite always, and on PostgreSQL and SQL Server when `DBDATA_TEST_POSTGRES` and `DBDATA_TEST_SQLSERVER` are set (`testenv/testenv.sh up mssql postgres`, then `eval "$(testenv/testenv.sh env dbdatakit)"`); `DBDATA_REQUIRE_POSTGRES=true` / `DBDATA_REQUIRE_SQLSERVER=true` make a missing engine a failure, not a skip.
- The sample package is `DbDataKit/tests/DbDataKit.Runner.TestPackage`, published once per run by `dotnet publish --no-build`; `DBDATA_TEST_PUBLISHED_PACKAGE` names a directory published beforehand, and `DBDATA_TEST_ARTIFACTS_PATH` the artifacts path of a build made with `--artifacts-path` (a machine whose disk is small builds elsewhere).
- The tests run the server from the build output of the server's own project (`server-output.txt` in the test output says where), so they use the task host the build put beside it.
