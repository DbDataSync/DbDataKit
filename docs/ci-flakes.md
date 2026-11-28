# Known flaky tests

A flaky test fails and then passes on a rerun with no change. This page lists every known one. The reviewer reruns a failed job only when its failure
matches an entry here; a failure that matches nothing is treated as real. A test leaves the list when its fix has merged and it has passed in four
weekly full runs since.

| Test | Signature of the failure | First seen | Fix |
| --- | --- | --- | --- |
| Runner `SchedulerTests.A_loop_that_stalled_and_wakes_up...(Sqlite)`, Windows | `TimeoutException: Gave up waiting for 1 runs to have started` | 2026-11-26 | cause not found; the test now names the items in flight and each loop's problems when it fails (CI flakes, #492) |
| `workspace-publish.spec.ts:130` | 420 s timeout waiting for `getByTestId('wsp-build')` | 2026-11-24 | fix merged (#492: the Workspace page kept the tab picked while a Delete was refreshing) |
| `DataChangeNotifierTests.The_call_is_signed_as_the_server_itself...` | `ArgumentOutOfRangeException: Index was out of range` | 2026-11-24 | fix merged (#492: the test's fake records calls under a lock) |
| Runner `RunnerDataChangedTests`, Windows | `409 Conflict` on the second `RunReports` | 2026-11-23 | fix merged (#492: the test asks again while the answer is 409) |
| Delivery `ConnectionChannelTests.No_secret_part_of_the_address_is_in_the_failure_the_row_or_the_log(failure: "timeout")`, Windows | the message recorded as `sent` | 2026-11-23 | fix merged (#492: the slow answer never answers) |
| `BuildChildIsolationTests.A_real_child_started_by_the_bundler_sees_none_of_the_hosts_variables`, Linux | `The bundler '/tmp/child-.../fake-bun' could not be started` (ETXTBSY) | 2026-11-25 | fix merged (#492: the fake bundler is written before tests run in parallel) |
