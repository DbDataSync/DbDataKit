# Plan 28: the runner, a published-to service for scheduled and batch work

Status: **proposed, high level** (2026-11-04, from the owner's direction in the plan 27 discussion). Not sliced. Its first consumer is
DbDataIntegration ([plan 27](27-knowledge-agents-integrations-work.md) section 4). Moving Sync and Build onto it is later work,
planned separately (section 8).

## 1. What it is

The serving layer (plan 20) runs **interactive** work from published definitions: queries, DbDataStore reads and writes, apps, agents,
sign-in. The runner is its counterpart for **scheduled and batch** work. A product publishes a package to it; the runner schedules the
package's tasks, runs each one **in a separate process by default**, and provides the features every scheduled task needs, so no product
writes them again.

```
          authoring back ends (definitions, code, packages)
   DbDataIntegration · later DbDataSync and DbDataBuild · others
          │ publish                         │ publish
          ▼                                 ▼
   serving layer (interactive)        runner (scheduled and batch)
          ▲                                 ▲
          └──── console (administration) ───┘
```

Why, in the owner's terms and plan 20's: a back end that authors and publishes can be patched or be down while the work it published keeps
running on its schedule; the shared features exist once; and work from several products can be chained on one runner.

## 2. Decisions (owner, 2026-11-04)

1. **Start with integrations.** The runner's first and only consumer at first is DbDataIntegration (plan 27 section 4). Sync and
   Build are considered for later migrations (section 8).
2. **Integrations are a product of their own, DbDataIntegration, that publishes to the runner.** The runner is just the runner; it is
   not DbDataIntegration, and it is not named for it.
3. **One pool can run any task; administrators decide the security posture.** Administrators configure their environment (network egress,
   firewalls, the accounts the runners use) and their runners to match the posture they want. The suite does not refuse a configuration
   because it could be a security concern. See section 5.
4. **Every packaging kind** in section 4 is wanted: scripts, compiled projects, a language-neutral protocol, and hybrids such as a .NET
   project whose APIs a .NET notebook orchestrates.
5. **Write logins on the runner are acceptable.** Build's write login leaving Build (plan 20 principle 3, plan 14 R2a) is not a concern
   when Build moves later: DbDataStore's data write login already lives outside DbDataStore's server.
6. **Product engines on the runner are acceptable, and can improve over time.** Sync and Build can later publish enough of their own
   runtime with their definitions (the way an integration publishes its project and its NuGet dependencies) so that one runner hosts their
   engines as a unified target.
7. **Moving state is the hard part and is designed separately.** Where Sync's and Build's run state lives after a move (section 8) needs
   design work of its own, before that migration is taken on. It is not part of the new services.

## 3. What the runner provides

| Feature | From | Notes |
| --- | --- | --- |
| Schedules: cron with time zone, interval, misfire policy, pause | `DbDataKit.Scheduling` (built, 13a M01) | the same evaluator Sync and Build use today |
| Triggers: the end of another run, a webhook, a file arriving, a DbDataStore change, an action from the serving layer, by hand | new | chaining runs of different packages, and later different products, on one runner |
| Work queue, leases, run lock, concurrency limits | `DbDataKit.Scheduling` (13a M02) | limits per package and per connection (Build's `max_parallel`, Sync's lanes are the precedent) |
| A process per task, with time and memory limits and cancellation | new; Build's child-process commands (D12) and Sync's TaskRunner are the precedent | in-process only when a package asks for it and an administrator allows it |
| Retries and timeouts | new | per task; back-off for errors a connection classifies as temporary (`ConnectionFailure`) |
| Logs and metrics | new, on the run model | structured, secrets redacted (the shared redactor) |
| Run history, health, notifications, retention | `DbDataKit.Monitoring` (13a M04, M05, M10) | the console's monitoring view (M11) shows runner groups like any server |
| Task state: checkpoints (where a task got to in a source), a ledger of processed files, idempotency keys, refused records | new: the progress store, a module of the runner group's state database ([plan 29](29-unified-state.md) section 4) | what integrations need to rerun without losing or repeating work |
| Connections and secrets, bound per channel | `connection/1`, the secret store (plan 13 W13), channels (plan 21) | outside systems as a connection kind is plan 27 question 10 |
| Run lineage | `DbDataKit.Lineage` (plan 18) | outside systems as nodes |
| Alerts by email, Teams, Slack, webhook | the delivery service (plan 22 section 5, not built) | |

## 4. What is published

A **package**: a manifest (its tasks, their schedules and triggers, the connections each needs, resource limits, the capabilities it
needs) and its code. Packages are versioned and published per channel; a runner switches to a new version atomically and keeps the last
good one (plan 20 principle 1).

The code is one of these kinds:

| Kind | What it is | Built where | Fits |
| --- | --- | --- | --- |
| **Script** | `.csx` on `DbDataKit.Scripting`, `#r "nuget:"` with a lock file | nowhere: compiled on the runner, runtime only (built today) | small tasks |
| **Compiled .NET project** | an ordinary C# project with its NuGet dependencies, its own tests and an IDE | in CI or on the authoring server, where the SDK is; published as assemblies, so a runner still needs only the runtime | real integrations |
| **Any executable, through the runner protocol** | a process that speaks the protocol in section 4.1, in any language (Python, for example) | by its author | teams with code in another language |
| **Hybrid: a notebook over a project** | a .NET project provides the APIs; a .NET notebook orchestrates them, cell by cell, run headless by the runner (the owner's example: ClrKernel) | the project as above; the notebook is published as it is | steps a person wants to read, adjust and rerun; the kernel and how a notebook reports progress are chosen in the detailed plan |

### 4.1 The runner protocol

How a task's process talks to the runner, the same for every kind. The .NET integration kit (plan 27 section 4.3) wraps it, so .NET code
never sees it.

- **Start:** the task's inputs, its run id, its channel, and handles for its connections (secrets resolved by the runner, never written to
  disk in clear).
- **During the run:** logs, metrics, progress, task state reads and writes, lineage records, a heartbeat.
- **From the runner:** cancel, with a grace period.
- **End:** an outcome (succeeded, failed with a classified failure, partly succeeded), counts and outputs.

The transport (standard input and output, a local socket, or gRPC) is for the detailed plan.

## 5. Pools and the security posture

**The rule (owner, 2026-11-04), for the runner and the suite as a whole:** we provide simple, clear options, guardrails and defaults, but
administrators decide what the system does. We do not block a choice because it could be a security concern. We make the choice
**explicit and clear** to the person making it, and **default to secure** where we can.

For the runner, that means:

- **One kind of runner.** Any runner can run any task. An administrator may label runners and route packages by label (for example, the
  runners with outside network access), but the suite does not require it.
- **The environment is the administrator's.** Whether a runner can reach the internet is set by its network, its firewall and its host.
  The runner reports what it finds (for example, "this runner can reach outside hosts") so the console shows it, and it does not try to
  enforce a network policy of its own.
- **Defaults are secure.** A task runs in its own process, under the runner's low-privilege service account, with time and memory limits,
  and with packages only from the allow-listed sources and lock files. A package that asks for more (to run in process, to run longer, to
  use a more privileged account) gets it only when an administrator turns it on.
- **Every loosened default is a recorded choice.** The console says what changes and why it matters, the administrator confirms, and the
  audit records who chose it and when.

## 6. Groups and availability

As serving groups (plan 20 section 6): runners in a **group** share state (in the structure of plan 29) on SQL Server or PostgreSQL through `DbDataKit.State`; leases on
the work queue let any runner take any task; a runner that stops loses its leases and its tasks are taken by another. The console pairs
with the group; a product that publishes to it pairs as a Publisher (plan 13 W12). SQLite is for a single runner only.

## 7. How the serving layer uses it

An action in the serving layer (plan 27 section 4.4) that needs work done outside its request, such as a call to an outside system or a
long job, **enqueues a task** on the runner and returns. The result comes back as a status, a DbDataStore change or a notification. This
keeps long and outward work off the serving layer by default; an administrator may still allow an action to do it directly (section 5).

Webhooks from outside systems arrive at a public address. Whether the serving layer receives and stores them for the runner, or a runner
listens itself, is open (section 10).

## 8. Later: Sync and Build on the runner

Not part of the new services, and planned separately when taken on.

1. **Engines as published runtimes.** Sync's replication engine and Build's executor are published with their definitions, the way an
   integration publishes its project and dependencies, so the runner hosts them as one more kind of package (decision 6).
2. **Schedulers move.** Sync's scheduler (13a M03) and the Build server's (M08) become the runner's; the console's pause and run-now (M11)
   act on the runner. The shared libraries carry over unchanged.
3. **State: to design first** (decision 7). Sync's watermarks, change-tracking positions and staging state; Build's tracking tables, which
   live on the target by design; run history in both. Whether they stay with the product, move to the runner's shared state, or are passed
   in and out of each run is the first question of that plan. When Sync adopts plan 29's pattern (state with the data and a unified copy), it defaults to
   keeping both (owner, 2026-11-04).
4. **Logins split by job.** The back ends keep read access for their interactive work (Build's plan previews, Sync's metadata browsing,
   connection tests); the runner gets the write logins (decision 5).

## 9. What this changes in other plans

| Plan | Change |
| --- | --- |
| 13 W10 | the scheduling and monitoring libraries get a host of their own; Sync and Build keep hosting them until section 8 |
| 20 | the runner is the scheduled and batch counterpart of the serving layer; principle 3 (Build's write login) is revisited when Build moves, decision 5 |
| 21 | packages are published per channel; a task in a non-production channel uses that channel's connections by default |
| 23 | the single-machine bundle includes a runner |
| 24 | runner groups upgrade rolling, after draining their leases |
| 27 section 4 | integrations publish to the runner; actions enqueue runner tasks for outward or long work |

## 10. Open questions

| # | Question | Notes |
| --- | --- | --- |
| 1 | The name and executable | "the runner" for now (`dbdatarunner`?) |
| 2 | The protocol's transport | standard input and output, a local socket, or gRPC (section 4.1) |
| 3 | Notebooks | which .NET notebook kernel, how a headless run reports each cell, what is kept from a run (outputs, a rendered copy) |
| 4 | Webhooks | received by the serving layer and handed over, or by runners directly (section 7) |
| 5 | Task state | **Decided** (owner, 2026-11-04): a state database for progress; designed as the `runner_progress` module of the unified state structure (plan 29 section 4) |
| 6 | Process limits | job objects on Windows, cgroups on Linux; a separate OS account per package, or one account per runner |
| 7 | Routing | whether labels are only an administrator's tool, or packages may require a label |
| 8 | State for Sync and Build | section 8, item 3; designed before that migration starts |
