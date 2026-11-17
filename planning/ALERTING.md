# ALERTING: alerts from the console and from each service

## Goal

A person hears about what went wrong without watching a screen: failed runs of Runner tasks, failed checks, approvals waiting, previews about to expire,
late data, a server that stopped answering, and a service whose console has not checked in for a while. The console checks the whole suite (it is the
least likely to be overloaded and the only place that sees every server); each service can raise its own alerts. Where each alert is raised is a proposed
decision, not settled.

## What exists today

- `DbDataKit.Delivery`: the outbox, the senders (email, Teams, Slack, webhook), rules, cool-down and recovered messages (`AlertService`); delivery channels
  are connections bound per release channel.
- The console's sender (`ConsoleDelivery.cs`) runs every minute. Its sources are the console's own run history (`RunHistoryAlertSource` over
  `new RunStore(state, "console")`) and approvals of agents; since the embedded runner went, no Runner task's failure reaches it.
- `ConsoleQualityAlerts` (a `BackgroundService`) reads the quality overview through the relay as the signed-in person; a background pass has nobody,
  so it sends nothing (quality alerts are paused, owner, 2026-11-16).
- The setup checklist still offers the step `quality-alerts` (`SetupItems.cs`).
- Each server evaluates its own health rules and keeps its own notifications (`DbDataKit.Monitoring`); the console only shows them.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| ALERTING-1 | Hide the `quality-alerts` setup step while quality alerts are paused (`SetupItems.Known`, the console's checklist); the alerts section of the Quality page says they are paused | none | S | `SetupItemsTests` and vitest of the checklist: no `quality-alerts` step; the Quality page shows the paused sentence |
| ALERTING-2 | Failed-run alerts for Runner tasks, raised where ALERTING-P1 says | TRUSTZONES-1 | M | with a fake clock on three engines: a failed Runner task sends one alert, again after the cool-down if still failing, one "recovered" when it passes |
| ALERTING-3 | Resume quality alerts: the background pass reads the Runner's quality overview with the console's own identity (or the Runner raises them, ALERTING-P1); the setup step comes back | TRUSTZONES-1, ALERTING-1 | M | `ConsoleQualityAlertsTests`: a failing check on a paired Runner sends one alert with no person signed in; the checklist step is offered again |
| ALERTING-4 | The whole-suite health check on the console: a server that stops answering, a group with no linked partner, a certificate near expiry, a backup older than N days, each an alert with cool-down | TRUSTZONES-1 | M | a test stops a paired Runner: one alert, one "recovered" when it answers again |
| ALERTING-5 | A service notices that its console has not checked in for a while (a setting, default 24 hours) and raises its own alert through its own outbox and the channels bound on it | ALERTING-P1 | M | a Runner with no console call for the setting's time writes one notification and one outbox row; a call from the console clears it |
| ALERTING-6 | The remaining alert sources: approvals waiting, expiring previews, late datasets | ALERTING-2 | M | one test per source with a fake clock: each sends once per cool-down and names the object |

## Needs from other subjects

- TRUSTZONES-1: the console's own identity toward a Runner.

## Proposed decisions

### ALERTING-P1: where alerts are raised (awaiting the owner; alerts do not necessarily move to the Runner, owner, 2026-11-17)

Option A, console only: the console reads every server's runs, checks and health with its own identity and sends every alert. Option B, each service
raises its own (its runs, its checks, a console that went quiet) through its own outbox and the delivery channels bound on it, and the console raises only
suite-wide ones (a server down, an unlinked group). Option C, both: services raise their own, the console raises suite-wide ones and de-duplicates
by alert key. Scenario: the console's machine is down overnight and a 2:00 refresh fails. Under A nobody hears until the console is back; under B and C
the Runner emails the on-call address at 2:00. Scenario: the Runner's machine is down. Under A and C the console sends "runner not answering"; under B
nobody does. Recommendation: C, with the delivery channels bound per service as today's bindings already allow.

## Shared areas touched

- `DbDataKit.Delivery`, `DbDataKit.Monitoring`, `DbDataKit.Quality.Runner` (`IQualityAlerts`), the console's `ConsoleDelivery.cs`, `SetupItems.cs`.
- A `delivery` state module in servers that do not have one yet (`scripts/check_state_migrations.py`), `docs/ui-editors.md` (alert rules row).
