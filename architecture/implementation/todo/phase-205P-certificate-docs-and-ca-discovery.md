# Phase 205P — certificate method docs, and discovering the CA and template

**Status:** designed, not yet built. Follows 204P in the build order only for the docs of `enroll`; `discover` itself is independent.

`dbdatasync config cert enroll` needs a CA (`--ca`, a `CASERVER\CA Name` string) and a template
(`--template`). Nothing tells an operator how to find either. Separately, certificate documentation is
one table in `docs/configuration.md` with no task-oriented pages.

## Where things stand

- **Docs:** certificate coverage is the subcommand table at `docs/configuration.md` (the
  `dbdatasync config cert ...` section) plus the `DbDataSync:Certificates:*` and
  `Kestrel:Certificates:Default:*` key tables. There is no "how do I get a certificate" doc.
- **Code:** `CertCommand.cs` already implements the methods:
  - `use-pem` / `use-pfx`: a file someone else supplies (Linux/macOS, also Windows).
  - `new-self-signed`: a managed file or the Windows store.
  - `enroll` / `renew` / `retrieve` / `templates`: AD CS.
  - `bind`: a certificate already in the Windows store.
- **The gap:** `templates` is the only discovery aid and it needs the CA string you are trying to find.
  `ResolveCaAndTemplate` fails with "Both a CA and a template are needed", and `SetupSteps.cs` prints
  `enroll --template <template>` with no help finding either value.

## Part 1 — Docs

Add `docs/certificates/`, one page per method plus an index. Keep the `docs/configuration.md` reference
table and link to the new pages from it and from `docs/getting-started.md`.

| File | Contents |
| --- | --- |
| `README.md` | Decision table: platform, whether there is a CA, whether renewal is automatic, where the key lives. Links to each method. |
| `self-signed.md` | `new-self-signed` for the Windows store and the managed file; `--store` / `--file`; the `AllowInvalid` implication; clients must trust it once. |
| `ad-cs-enroll.md` | The main page; content below. |
| `existing-windows-store.md` | `list`, then `bind --thumbprint`; `--allow-invalid`; the `Kestrel:Certificates:Default:*` keys. |
| `pem-and-pfx.md` | `use-pem` / `use-pfx`; the unencrypted-key limitation; Linux/macOS. |
| `renewal-and-expiry.md` | `renew`; `ExpiryWarningDays` notifications; restart required; managed-file renewal service vs. AD CS re-enroll. |
| `troubleshooting.md` | Needs Administrator; pending approval; discovery failure reasons; HTTP.sys not integrated. |

Each page: when to use it, prerequisites, commands, config keys written, verify with `cert status`,
common errors.

### `ad-cs-enroll.md` content

- **CA string.** Format `CASERVER\CA Name`, for example `ca01.corp.local\Corp Issuing CA 1`. Find it by
  running `certutil` with no arguments on a domain-joined machine (the `Config:` line), or `certutil -dump`;
  or ask the PKI admin; or read the machine name and CA name in `certsrv.msc`.
- **Template.** Use the template *name*, not the display name. `dbdatasync config cert templates --ca "CASERVER\CA Name"`
  (unelevated), with `certutil -CATemplates -config "CASERVER\CA Name"` as a fallback.
- **Template requirements.** Server-authentication EKU, requester supplies the subject/SAN, and the
  enrolling account has Enroll permission.
- **Flags vs. config.** `--ca` / `--template`, or `DbDataSync:Certificates:CaConfig` / `Template` in
  `dbdatasync.config.yaml`.
- **Elevation and approval.** `enroll` needs Administrator; a template requiring approval reports a
  request id and exits (not an error); finish with `config cert retrieve`.

**Verify before stating as fact:** the `certutil -CATemplates` command and the template requirements
against `AdcsEnrollment.cs` and `CertificateTemplateCatalog.cs`. Anything that cannot be confirmed
should say "typically".

## Part 2 — Discovery

Preferred approach: **auto-detect from AD**, not auto-running `certutil`.

- The repo already queries AD directly (`CertificateTemplateCatalog` reads the enrollment-services
  objects), so a native lookup is consistent and testable, and avoids parsing `certutil`'s localized
  output.
- Discovery reads AD. Its only write is the single-result config save described below, and that is the
  same kind of write `config cert bind` already makes.

### Proposed command: `dbdatasync config cert discover`

No flags beyond the repo-root option every config command already takes. Not store-writing, so it runs
unelevated like `templates`.

**CA:**

- **Exactly one CA found:** write `DbDataSync:Certificates:CaConfig` to `dbdatasync.config.yaml`
  automatically, committed the same way other config writes are, and say what was written.
- **More than one CA found:** write nothing. Print each as `CASERVER\CA Name`, then the command that
  would save the chosen one, for example:
  `dbdatasync config set DbDataSync:Certificates:CaConfig "CASERVER\CA Name"`.
  After saving, the user runs `discover` again to get the template step below.
- **`CaConfig` already set:** never overwrite it. If it matches a discovered CA, say so and continue to
  templates. If it does not, print the discovered CAs and the same save command, and say the configured
  one was left alone.
- **None found, or AD unreachable:** say why using the existing `TemplateListReason` values and point to
  the manual `certutil` steps in the docs.

**Template** (once there is a CA, discovered or already configured), same rule:

- **Exactly one published template:** write `DbDataSync:Certificates:Template`.
- **More than one:** print them and the matching `config set ... Template "<name>"` command.
- **None:** say so.

When both values are saved, print the exact `enroll` command to run next.

Whether the config key is writable through `config set` as written (and whether `config set` is the
right command to print) needs checking against `CertCommand`/the config command before the doc is
final.

### Template suitability: not in the first version

An earlier draft marked templates as suitable or not (server-authentication EKU, requester supplies the
subject, current user may enroll). `CertificateTemplateCatalog` today only reads the template *names*
from the enrollment-services object. Judging suitability means new AD reads of each template object
(its EKU, name flags and ACL) and is a separate piece of work.

Without it, a CA that publishes many templates will always land in the "print the list" branch, so
auto-writing the template will only help where exactly one is published. That is accepted for the first
version. Suitability filtering is the follow-up that would make the template step automatic more often.

### Smaller integrations

- `ResolveCaAndTemplate`: when `--ca` or `--template` is missing, the error says to run
  `dbdatasync config cert discover`. It does not pick a CA itself.
- `SetupSteps.cs`: the `enroll` instruction line mentions `discover`.

## Part 3 — Tests

- Unit tests for the `discover` decision logic behind a seam (AD is not available in CI), following how
  `CertificateTemplateCatalog` is already tested. Cases: one CA writes `CaConfig`; several CAs write
  nothing and print the save command; `CaConfig` already set and matching, set and different (left
  alone); none found or AD unreachable; one template writes `Template`; several print the list and
  command; none.
- `ResolveCaAndTemplate`: the new error text pointing at `discover`.
- Non-Windows refusal, and unelevated-allowed, matching how `templates` is covered.
- If the repo has a docs link/command checker, run it; documented flags should match `CertCommand`'s
  usage text.

## Sequencing

1. Docs first (no code). Index, `ad-cs-enroll`, then the rest.
2. `discover` command and tests.
3. Hints in `ResolveCaAndTemplate` and `SetupSteps`.
4. Cross-links from `configuration.md` and `getting-started.md`.

Docs and code as separate commits.


## Open questions

- Is template suitability filtering (EKU, requester-supplied subject, enroll permission) worth adding to
  the first version, given it needs new AD reads of template objects? Without it, the template step only
  auto-writes when a CA publishes exactly one template.
