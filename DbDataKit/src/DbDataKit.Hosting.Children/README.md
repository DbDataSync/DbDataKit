# DbDataKit.Hosting.Children

Supervised child processes for Sync's TaskRunner and Proxy's x86 worker.

- `ChildProcessHost.Launch(spec)`: binds the transport listener first, starts the child with a per-launch token, endpoint and parent identity in the environment (never argv), assigns it to a Windows Job Object (`KILL_ON_JOB_CLOSE`, optional memory/CPU caps). Disposing the `ChildProcess` kills the tree. On Linux there is no job: tree kill on dispose plus the child-side `ParentDeathMonitor`.
- Child side: `ChildEnvironment.TryRead()`, `ChildTransports.ConnectAsync(info)`, `ParentDeathMonitor.Start(...)` (guards against pid reuse using the parent's start time).
- Transport: named pipe (Windows, `CurrentUserOnly`) / Unix-domain socket (0700 dir, 0600 socket) / loopback TCP. All use a token handshake; the TCP listener stays bound for the launch, so there is no allocate-then-release port race (Proxy S-12).
- `RestartPolicy`/`RestartTracker` (pure backoff, window, stable-run reset) and `ChildSupervisor` loop.

- `ChildEndpointKind.None` (spec `Transport`): launch without a handshake listener for children that host their own server (for example Kestrel on a host-chosen port). Job object, kill-on-dispose, parent identity variables and output capture still apply. `OnOutputLine`/`OnErrorLine` receive child output; output is always drained, so a chatty child cannot block on a full pipe.

Known limit: the child is assigned to the job just after `Process.Start`, so a grandchild spawned in that instant could escape; creating suspended needs a custom CreateProcess and is a possible follow-up.
