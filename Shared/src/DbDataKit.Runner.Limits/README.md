# DbDataKit.Runner.Limits

Time, memory and CPU limits for a task's process, and a report of what this machine can enforce (the runner's executor calls it; the posture rules are in the `DbDataKit.Runner` README).

`ProcessLimits.Apply(Process, LimitSpec)` picks the mechanism and returns a `LimitHandle` (`Enforcement`, `Exceeded`, `ExceededInfo` with the observed value):

* **Windows:** a job object (the one `DbDataKit.Hosting.Children` already uses), with memory and CPU caps.
* **Linux, cgroup v2:** a child cgroup (`memory.max`, `cpu.max`, removed after exit) when the runner's account has a delegated subtree with the controllers; the library finds out by creating a probe child.
* **Otherwise a watchdog:** it samples resident memory every 250 ms and kills the process tree over the limit. On Linux the sample is the sum of `VmRSS` over the process and all its descendants (read from `/proc`; a page shared between processes counts in each, which errs toward the limit), so an allocation in a child or grandchild is caught; elsewhere it is `WorkingSet64` of the process itself (Windows uses the job object, which covers the tree).

The runner **reports** which one it uses and does not refuse to run without cgroups: `LimitCapabilities.Detect()` returns `memory_limit` and `cpu_limit` (`enforced` or `watched`), `limit_mechanism`, `os` and `network` (`outside` or `none` by a cancellable connect test to configured hosts, `unknown` when none are configured) and `OdbcCapabilities` the installed ODBC drivers (`odbc:<id>`). The runner stores them as capabilities and as read-only labels, and the console shows them. Whether a runner can reach outside hosts is its network's business; the library does not enforce a network policy.

Tests: the watchdog is the tested path (an allocating child is killed, and so is a parent whose grandchild allocates); the cgroup test skips on a machine without a delegated cgroup v2 subtree and has to run where one exists.
