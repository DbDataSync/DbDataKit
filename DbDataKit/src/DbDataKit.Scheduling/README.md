# DbDataKit.Scheduling

When a job is due, as a pure function (plan 13, W10). No product logic: a job is "run this with these inputs", and the product decides what that means.

* `Schedule`: an `Every` interval (counted from the start of the last run) **or** a five-field `Cron` read in a `TimeZoneId` (IANA or Windows id; daylight saving follows the zone), a `Misfire` policy and its `MisfireGrace`. `Validate()` says what is wrong in words.
* `ScheduleEvaluator.Evaluate(schedule, since, now)` returns a `ScheduleDecision`: `Due`, `NextDue`, how many scheduled times were `Missed`, and `HandledUpTo`, the point to store afterwards. `since` is the start of the last run, or the `HandledUpTo` of the last decision.
  A job that never ran (`since` null) is due. Several missed times are one run (`RunOnce`, Sync's behaviour) or, with `Skip`, none when the latest is older than the grace, with `HandledUpTo` set to it so they are not judged again.
* Daylight saving, as Cronos does it: a time inside the spring-forward gap runs right after the gap; a fixed time in the repeated hour runs once, at its first occurrence; an hourly cron runs in both passes. Tests pin each on New York and Berlin.
* `RunGate.ShouldRun(enabled, paused)`: durable intent and temporary hold.

The work queue with leases and the run lock (M02) and Sync's scheduler on these packages (M03) are the next slices.
