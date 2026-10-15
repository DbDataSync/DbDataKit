## Hosting (Plan 03)

Branch: worktree-agent-ad4903181a885165d. Packages in `shared/src`: DbData.Hosting.Abstractions, DbData.Hosting, DbData.Hosting.WindowsService, DbData.Hosting.Systemd, DbData.Hosting.Certificates, DbData.Hosting.Children, DbData.Hosting.Cli (tests in `shared/tests`).

- Done: all six deliverables with unit tests (Windows-only tests are skipped elsewhere; the real-SCM mutating test needs `DBDATA_ADMIN_TESTS=1` and an elevated prompt and was not run here).
- Run tests from `shared/`: `dotnet test --project tests/<Package>.Tests` (`shared/global.json` selects the Microsoft.Testing.Platform runner required by xunit.v3 on the .NET 10 SDK).
- Stubbed: ADCS enrolment (interface only), UAC relaunch, logging/redactor wiring in `UseDbDataHost`, job assignment is post-start (tiny race documented).
- To reconcile at merge: `IAclPolicy`/`WindowsAclPolicy` here vs the Secrets agent's AclHelper (`DbData.Secrets.Windows`); `shared/global.json` may also be added by other agents.
