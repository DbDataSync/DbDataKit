# Plan 28: DbDataRunner, what is still open

Status: **in progress, what is left** (audited 2026-11-09). The runner is built, and what is true of it is documentation beside the code: `DbDataRunner/README.md` (the host, publishing, groups, the security posture, the console's embedded runner), `Shared/src/DbDataKit.Runner/README.md` (the engine's rules), the READMEs of `DbDataKit.Runner.Abstractions` (the `package/1` manifest), `DbDataKit.Runner.Kit` (what a job writes), `DbDataKit.Runner.Protocol` and `DbDataKit.Runner.Limits`. Its open slices are in [28a](28a-runner-slices.md); Sync and Build on the runner are [28c](28c-engines-on-runner.md). This page keeps only the owner's decisions that bind work that is not built yet, and the open questions.

The runner is the scheduled and batch counterpart of the serving layer ([plan 20](20-serving-layer.md)): a product publishes a package to it, and it schedules the package's tasks, runs each in a process of its own and provides what every scheduled task needs. Its first consumer is DbDataIntegration ([plan 27](27-knowledge-agents-integrations-work.md) section 4).

## Decisions that still bind unbuilt work

1. **Webhooks are received by the serving layer** (owner, 2026-11-04), which is already the public endpoint: it checks the signature, stores the body as a payload and adds an entry to the runner's inbox, which a task takes. Runners need no inbound access from outside unless an administrator chooses otherwise. The runner's end is built (`POST /api/v1/webhooks/...`, the inbox); the serving layer's end is plan 20's (28a R13).
2. **An action in the serving layer that needs work outside its request** (a call to an outside system, a long job) **enqueues a runner task and returns**; the result comes back as a status, a DbDataStore change or a notification. An administrator may still allow an action to do the work directly. The serving side is plan 27 section 4.4; the runner side is a run-now with inputs (built).
3. **Agents on a runner** (owner, 2026-11-06): an unattended agent step runs in the task's own process with the agent runtime as a library; the agent definition is published with the package as a dependency, and its model connection is a slot of the package. **Evaluation runs** (plan 27 section 3.4) are tasks on the console's embedded runner. The embedded runner's `evaluation` kind is a placeholder until plan 27 builds them.
4. **Sync and Build move onto the runner as published runtimes** (owner, 2026-11-04): one runner hosts their engines as one more kind of package, their schedulers become the runner's, their state moves into modules of the unified state **and** stays on the target (`both`, the target as primary for each), and their write logins move to the runner while the back ends keep read access for interactive work. The design is plan 28c.
5. **Alerts by email, Teams, Slack and webhook** use the delivery service (plan 22 section 5, not built).

## Open questions

| # | Question | State |
| --- | --- | --- |
| 1 | Notebooks (a .NET notebook over a project, run headless; the owner's example is ClrKernel) | **Deferred** (owner, 2026-11-04): decided when the notebook kind is built, after scripts and compiled projects; before then check that the kernel is maintained and runs headless |

## Why this page is still here

Plan 28c links to it. When plan 28c has been cleaned up (its rows are being finished by other work), delete this page: its decisions then belong to plans 20, 22, 27 and 28c.
