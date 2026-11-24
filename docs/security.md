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

## When a safety rule is worth adding

A rule that refuses, warns or asks for a confirmation is added only when it stops a mistake that can actually happen, or protects something a person
does not already control. Before adding one, answer three questions:

1. **What does it stop?** Name the mistake and who makes it. "Ana runs a test against production because the channel picker chose it" names a
   mistake. "Production should be protected" does not.
2. **Does the person already have the access?** If they hold the login, the role or the file, the rule cannot keep them out. It only changes which way
   they take.
3. **What do they do when it refuses?** If the quickest way around the rule is worse than what the rule refused, do not add it.

Rules the suite keeps:

- A non-production channel never inherits production's connection. A test run cannot write to production because a binding was missing.
- A read in a production channel is warned about before it starts.
- An error found by a preflight check needs a confirmation before the version is activated, and the confirmation is recorded with the person's name.

Rules the suite does not add:

- Refusing to store a login for a production channel from the Workspace screens. A person who has production's password can store it under a
  development channel instead. The refusal changes where the login is recorded, not who can use it.
- Asking for a role on the Workspace before the console deploys a build, when the person may already publish on the Runner. A publisher who is
  refused can still upload the package's folder on the Runner's Packages screen, which skips the build record, the console's signature and the
  preflight check. The rule would push them onto the way the suite can trace least.
- Hiding a failure's full message from an administrator. They are the person who has to fix it, and without the message they guess.

Who may do what is set by the roles of each service. The roles are deliberately broad for now, and a rule does not narrow them on its own.
