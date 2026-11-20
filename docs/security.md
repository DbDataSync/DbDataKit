# Security
<!-- reviewed: 2026-11-20 -->

How the suite's design answers its main risks. Findings of earlier code reviews are in `docs/security-review.md`.

| Risk | What the suite does |
| --- | --- |
| The secrets key is lost (a disk fails, a machine is rebuilt) | Back the key up apart from the secret store. Without the key the store cannot be read. `secret export` writes every secret to a file under a passphrase, to move them to another machine. |
| Another account on the machine can read the secrets key | A service refuses to read a key file or folder that other accounts can reach; the message names the path and the command that fixes it. On Windows the key is also protected by DPAPI and an access list. In a container the key comes from the platform's secret mechanism (`secrets.keyFile` or `DBDATAKIT_SECRETS_KEY_FILE`). |
| The control service's signing key is stolen | It would act on every service paired with that control service. The key stays in the control service's protected store, each pairing's role ceiling limits what it can do, and `dbdatakit instance rotate-key` or unpairing on each service cuts it off. |
| A person writes files in the Workspace that later run | Each draft is one person's git worktree. What runs is a published version of a package, and publishing needs the publisher's role. |
| The control service holds what it needs to manage every service | Each pairing is per service, with short-lived tokens for one service and a role ceiling. The control service does not hold a login to an external system: each service resolves its own secret references. |
| The management API weakens the Gateway | It is on a separate listener, over HTTPS with a pinned certificate. Every call carries a signed token that the Gateway checks, and the Gateway enforces the roles itself. The management code is not on the query path. |
| Two people change the same thing through two control services | Every write is checked against the version it was based on, and refused when that version changed. |
| Monitoring stops when the control service is down | Each service evaluates its own health rules and keeps its own notifications. The console only shows them. |
| A scheduled task runs twice | A Runner group's work queue gives one instance the lease of a due run and holds a run lock. A Build refresh started while another is running follows its `on_busy` setting. |
