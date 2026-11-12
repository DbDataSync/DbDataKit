# Channel bindings

One concept for every place the suite binds a **name** to a **connection** per **release channel** (owner direction, 2026-11-11: the suite has no two designs, formats, screens or pieces of code for the same thing). Plan 27a IX23 built it; plan 22a GP18 (decision D10) and plan 27a AG05 and AG18 are its first users.

## The concept

A **binding** is: a **slot** of a **kind**, and for each release channel a **connection** (for a model, which of its models), or for a model slot an **ordered list** of them (the first is used, the others take over when it is down).

| Kind | The slot is | A release channel takes | Kept in |
| --- | --- | --- | --- |
| `model-chat` | the name an agent asks for (`default`, `review`) | a list of `{connection, model}` of a connection of kind `model-provider`, purpose `chat` | `bindings.yml`, section `models:` |
| `model-embeddings` | the name `embeddings` (a channel's knowledge index) | exactly one `{connection, model}` of a provider with purpose `embeddings` (an index has one model) | `bindings.yml`, section `models:` |
| `delivery` | a delivery channel's name (`alerts`, `mail`) | exactly one connection that can send: `kind: smtp`, or `kind: http` with a `webhook:` section | `bindings.yml`, section `delivery:` |
| `runner` | a connection slot of a runner package (`package/1`, frozen), named `package/slot` | one connection (a name or a secret reference) | the runner's own store (`definition_binding`); **changed here** (the console hands the runner the connection first) and read back in the same shape |

The connection is always a `connection/2` file of the console (`connections/<name>.yml`); a binding names it, never repeats it. Secrets stay references (`secret:dbdatakit:<service>:<kind>:<name>[:<part>]`) in the connection file and are never in a binding.

## The one rule

For a slot and a release channel, **Resolve** gives:

1. the release channel's **own entry**;
2. else the **`*` entry**, if the file has one: written on purpose, it means "every release channel without an entry of its own";
3. else **unbound**, with words that name the slot, the release channel and where the slot *is* bound.

A release channel **never takes another release channel's entry**, production's included, and an absent release channel (a caller that has none) is `prod`. A bare value (`mail: smtp-main`, or a list for a model slot) binds `prod` only.

Why one rule and not "inherit for reads, not for writes": plan 21 says writes are never inherited, and the audit of the three mechanisms found **no implicit inheritance in any of them**: the runner never inherits (a slot unbound in a channel refuses the version that needs it), the delivery channels bound `*` only when written, and the model slots read `*` only when written. So the shared library implements exactly that and nothing more; a rule for read-only slots would be a fourth behaviour with no user. If a read-only kind ever needs implicit inheritance, it is a decision of its own, taken then, with a flag on the kind.

`*` is explicit, so it is visible in the file and on the screen ("* (every other)"). A `*` that binds production's connection for a slot that **sends or writes** is the case the next section is about.

## Production's connection in a non-production release channel (IX17)

For a slot that sends or writes (a delivery channel, a runner slot), a **non-production release channel that uses a connection production uses** is an explicit, confirmed, recorded choice: a test message that reaches production's Slack is what the rule exists to prevent. **Model slots stay exempt** (owner default, 2026-11-11): a model slot only reads (a prompt leaves; nothing in the world changes; what may leave is the agent policy's rule), so it needs no confirmation.

* The server that keeps the binding refuses a save that would make one (HTTP 409 with `needsConfirmation`, each item naming the scope, the slot, the release channel, the connection and the effect) until it is confirmed, then records it as the posture choice `production_binding_on_non_production` (the catalogue entry of `PostureSetting`), scoped `bindings/<section>/<slot>/<release channel>` (who, when, a note), and in the audit log. For a runner slot the section is `runner` and the slot `package/slot`.
* **Where the record lives (one mechanism, one table shape, one piece of code: `PostureChoices` and `PostureGuard` over a `posture_choice` table).** Each server records the choices of **its own** bindings in **its own state**, because each is the one that holds the binding and enforces it: the **Runner** in its own `runner.posture_choice` (its slots: the Runner refuses the bind itself, so a console, the command line or the API cannot get around it), the **console** in `bindings.posture_choice` (the `bindings` module, install scope, always present; its delivery channels). Neither reads the other's. The earlier `bindings-posture.json` of the console is **moved into the table at the next start** (who, when and why kept, the file renamed `bindings-posture.json.moved`, audited as `bindings.posture.move`; a file that does not read stays and the audit names it).
* **Why two tables and not one (the owner asked for one place and kept two, 2026-11-12).** A single physical table would have to be read and written by two servers. If it lived in the **console's** state, the Runner could not enforce the rule itself (it would have to read the console's database, which the separation of the two servers forbids), and a remote runner bound through its own API or command line would bypass it. If it lived in the **Runner's** state, a console without the embedded runner has no runner module and no database of the runner's to write to, and a delivery channel's choice is not the runner's business. What **is** unified: one setting (`production_binding_on_non_production`), one scope format, one table shape and one piece of code, and the console **reads** the runner's choices through the relay (the `confirmed` of each binding in `GET /api/v1/definitions/integration/<name>`) so the Bindings page shows both in one place. The rule, until the owner decides otherwise: **each server keeps the choices of the bindings it holds.** The owner kept the two tables (2026-11-12); the combined list is read-only.
* **Two tables, one combined, read-only list (owner, 2026-11-12).** Enforcement stays local to the server that holds the binding; the console shows both tables as one list (`GET /api/bindings/posture`, below). The earlier "decision open" about a single table is closed: two tables, one view.
* Moving the slot to its own (sandbox) connection takes the choice back.
* `*` with no `prod` entry binds production's connection for every other release channel and is the same case, scoped `.../*`; **a delivery `*` that binds production's connection is flagged like an explicit entry** (owner default, 2026-11-11). A runner binds per channel and has no `*`.
* The Runner compares a channel with production's binding of the same slot, whichever is bound last: binding `dev` to what `prod` uses, or moving `prod` onto what `dev` already uses, needs the confirmation; a bind that needs none is not blocked by one that does (the screen's answer lists what is left to confirm, and the channels that needed none are bound).
* What is **not** built: a hand-edited `bindings.yml` is checked by the screen (it shows "nobody confirmed it") but not refused at send time; a task's own outbound call is held to the rule only at the start of its run (see below), not per call.
* **The runner's backstop (IX17).** Besides the bind-time confirmation, the runner checks when a run starts (`RunnerSlotResolver.Handles`): a task in a channel other than `prod` is refused the connection `prod` binds the same slot to unless the **same** `runner.posture_choice` record exists (scope `bindings/runner/<package>/<slot>/<channel>`). `dbdatarunner package allow-production-binding <package> <slot> <channel> --confirm|--revoke` writes and revokes that record through the same guard as the Bindings page; there is no second mechanism.

## The file: `bindings.yml` (format `bindings: 1`)

One file in the console's directory, read by one strict reader (`DbDataKit.Connections.Bindings`, in `DbDataKit.Connections.Abstractions`), with the file and line of every problem:

```yaml
bindings: 1
models:
  default:
    prod: [ { connection: anthropic-main, model: claude-sonnet }, { connection: ollama-local, model: "llama3.1:8b" } ]   # primary, then fallback
    "*":  [ { connection: ollama-local, model: "llama3.1:8b" } ]                                                          # every release channel without its own
  embeddings:
    prod: { connection: ollama-embed, model: nomic-embed-text }
delivery:
  mail: smtp-main                            # a name alone binds prod only
  alerts: { prod: slack-ops, test: slack-sandbox }
```

**Why one file and not two with one reader.** The models file (`models/_slots.yml`) and the delivery mapping (`delivery.channels` in `settings.yaml`) differed only in where they were written. One file is the smaller honest design: one reader, one writer, one screen, one place to read "what does `test` use?", and `settings.yaml` goes back to settings (the delivery section keeps the retry policy, the lease and the limits). Both earlier forms were **open and unreleased**, so it is a clean break, not silent: `models/_slots.yml` is reported by the screen naming the file, `model-slots:` and `provider:` are refused with `DBD-BIND-003` naming the new form, and `delivery.channels` in `settings.yaml` is refused naming the file and `bindings.yml`.

The checker (`DBD-BIND-004`) names a connection that does not exist or does not fit the slot (the wrong kind, the wrong purpose, a model the provider does not allow, a connection that cannot send), with the line.

Code: `Resolve(slot, releaseChannel)` returns the entries and where they came from (`prod`, or `*`), or the `Unbound` words; `BindingSet.Unbound(kind, needed, releaseChannel)` is what a promotion that needs slots is refused with.

## The screen


Below the slots the page has the **compare** panel (a server picker, an "only what differs" switch, per item **Adopt** and **Publish** for an administrator, `BindingCompare.tsx`) and the **posture choices** list (read only, each choice once with its holders): see "Compare, adopt and publish" below.
**Bindings** (`/bindings`, `Shared/web/console/src/pages/BindingsPage.tsx`) lists every slot of every kind with, per release channel, the connection (and model), whether it can be used now, and for an administrator **Test**, **Change** (a connection per release channel, the fallback order, a release channel added or removed), **Remove the slot**, and the confirmation of the IX17 choice. The Models tab of Agents and the Channels tab of Delivery show **the same panel** for their kinds (no second editor); the file itself can be edited as text on the same page. The runner's package slots are read from the runner (`/relay/console-runner/api/v1/definitions/integration`) and shown in the same shape with the runner's own Test; an administrator **changes** a runner slot's connection per channel (a console connection from the list, or a name the runner knows), sees the runner's copy of it against the console's file with **Send again**, the references the runner cannot resolve, and the IX17 confirmation (below), through `PUT /api/bindings/runner/<server>/<package>/<slot>`.

## The runner: what it keeps and how the console hands it a connection

The Runner is a separate server and **does not read the console's `connections/` folder**. A runner slot is bound (as `package/1` and the binding store say: `slot -> name`, per channel, nothing frozen changed) to a name the runner knows: a file `connections/<name>.yaml` in the runner's own directory (database slots, as before), a secret reference, or **a copy of a console connection** the console handed it:

1. **The hand-over.** When an administrator binds a runner slot on the Bindings page, or presses **Send again**, the console puts the connection's **definition with its secret references, never a value** on the runner: `PUT /api/v1/connection-copies/<name>` (an administrator's call, relayed for a registered runner, in process for the embedded one), `GET` lists them. The runner checks the text with the shared strict loader (a secret written as itself is refused without being echoed), keeps it and audits `runner.connection.put` with the **name and version only**. A name the console does not keep (a runner's own connection, a reference) is bound by name and nothing is sent.
2. **The runner's own store.** The copy is a row of `connection_copy` in the runner module (migration 5: name, version, kind, definition text, who sent it, when): the smallest honest store, a table beside `definition_binding` in the state the runner already has (a group shares it), not a second kind of file. It is additive: `package/1`, the slot declaration and the binding store are as they were. The **version** is `sha256:` and the first 16 hex digits of the definition's text (line endings and a byte-order mark do not count), computed by the same code (`ConnectionCopyVersion`) on both sides, so "is the runner's copy the console's" is a comparison of two short texts.
3. **Secrets resolve on the runner.** A reference resolves in the runner's own secret store, environment or credentials directory (`DbDataKit.Secrets`), like every login; the console never sends a value with the definition (`secret send --to runner`, plan 28c E20d, puts a value in the runner's store). `GET connection-copies` says for **each reference whether it resolves on this runner** (`resolves`, `problem`, never a value), and the Bindings page says so on the slot's row ("The runner cannot resolve `secret:...`"); the reply to a save or to Send again lists the same. A reference that is missing at run time is the existing failure naming the slot, the connection and the reference.
4. **Slots resolve from the copy.** `RunnerSlotResolver` finds the name in the catalog: the runner's own `connections/` folder first (an operator's file on this machine is the stronger statement; the list says `shadowed_by_file`), then the copies. A copy of kind `http` gives a grant with the address, the sign-in method (`api_key`, `basic`, `oauth2_client_credentials`, `client_certificate`, `none`) and the secrets resolved here, and `OpenHttp` makes the client with `DbDataKit.Connections.Http` over the runner's own secrets. A copy of another kind that is not a database (`smtp`, `model-provider`) is refused for a slot, naming the kind. The grant also carries the definition text and every secret of it resolved on the runner (`HttpDefinition`, `HttpSecrets`, for this run only), and the Kit makes the signed-in client in the task: `OutsideHttp.Open(grant, run)`, or a slot typed `Slot<OutsideHttp>`.
5. **A copy is what was sent.** Changing the console's connection does **not** change the runner's copy until it is sent again; the Bindings page shows the copy's version against the console's (`current`, `stale`, `missing`, a copy the console no longer keeps, or a name the runner knows itself) and offers **Send again**: the same rule as a package, what runs is what was published.

The posture confirmation (above) is the Runner's own: `PUT /api/v1/bindings/<channel>/integration/<package>` refuses with 409 and `needsConfirmation` until the body's `confirm` names each scope, and the Runner records the choice in its own `posture_choice`. The console's `PUT /api/bindings/runner/<server>/<package>/<slot>` sends the definitions first, binds each channel (`prod` first) and passes the 409 on.

A channel can be **emptied** (`DELETE /api/v1/bindings/<channel>/integration/<package>/<slot>`, `runner.package.unbind`, the posture choice it held taken back), refused (409) while the version the channel serves or has staged declares the slot. Not built: a runner screen of the copies beside the Bindings page, sending a secret **value** from the Bindings page (the command line does it).

## Compare, adopt and publish, and the combined posture list (IX25)

The Bindings page ends with two panels, both over `ConsoleBindingCompare` (`DbDataKit.Server`), which has **no path of its own** to another server: every read and write of a Runner or a Gateway is plan 30's shared route (`DefinitionsClient` over the link `ConsolePromotions` makes **as the signed-in person**, so the server decides what the person may do and records the person as the actor).

**Compare** (`GET /api/bindings/compare?server=<id or name>`, any signed-in person; the server is the console's own runner `console-runner`, a registered Runner or a registered Gateway). It lists **items** and marks each `same`, `differs`, `only-here` (the console holds a value, the other server none) or `only-there`:

| Item | The console's value | The other server's value | Kinds of server |
| --- | --- | --- | --- |
| `binding`: a slot of a definition the other server holds, in a release channel | what `bindings.yml` resolves for the slot in that channel (its own entry, else `*`) | the group's binding row (`bound_to`, read with `GET definitions/{kind}/{name}`) | a Runner (`integration`, `agent`), a Gateway (`agent`) |
| `connection`: a console connection and the copy of it a Runner holds | the version of the file (`ConnectionCopyVersion`) | the version of the runner's copy (`GET connection-copies`, which also carries the definition text, secret references only) | a Runner |

Which slots correspond is a naming rule, not a guess: a slot of another server's definition named **`model:<slot>`** (the name the registry gives an agent's model slot) stands for the console's model slot of that name, and **`delivery:<channel>`** for the delivery channel of that name; any other slot (a package's own `package/slot` connection slot) is bound on the other server only and is not an item. A binding's value is one text, `BindingValueText`: `connection/model, connection/model` for a model slot (a model slot's choices in order, the primary first), a connection's name for a delivery channel. A Gateway keeps database connections (`data-source`) and the console keeps connections of outside systems, so the two share no connection: a Gateway is compared for its bindings only. A group's description lists the bindings of the channels that have a version served, so a channel the definition was never published to is not listed.

**Adopt** (`POST /api/bindings/compare/adopt`, administrators) makes the other server's value the console's: a binding is written into `bindings.yml` through the one writer (`ConsoleBindings.SetSlot`, so the strict reader and the checker decide and a connection that does not exist is refused naming the line; a value that would make production's connection serve a non-production release channel is refused, 409, and is bound on the Bindings page where it is confirmed), a connection into the console's `connections/<name>.yml` through `ConsoleConnections.Save`. It records who in the audit (`bindings.adopt`, `connection.adopt`: the person, the server, the value, what it replaced). **Publish** (`POST /api/bindings/compare/publish`, administrators) puts the console's value on the other server: a binding with `PUT /api/v1/bindings/{channel}/{kind}/{name}` (the console holding none for it empties the slot there with `DELETE .../{slot}`), a Runner's connection by the connection copy the Bindings page already sends (`PUT connection-copies/{name}`), the hand-over described above and not a second one. The other server audits it as it audits any bind, and the console records `bindings.publish` / `connection.publish` (who, which server, the value, what it replaced). The body of both names the item and the other side's value the screen showed (`expected`); when the other side changed since, nothing is done (409, "changed since you looked"). The other server's refusal comes back in its words. A Gateway that requires signed versions of a kind refuses what carries no signature; a binding is not a signed version, so publishing a binding is not affected.

**The combined posture list** (`GET /api/bindings/posture`, any signed-in person) reads the console's table (`bindings.posture_choice`), the embedded runner's and each registered runner's (`GET posture-choices` through the relay as the person) and merges them with `PostureListing`: **a choice is listed once** for its setting and scope whichever table holds it, with every holder named (`heldBy`); when several hold it the earliest record is shown. A runner that does not answer is named in `sources` and the rest is listed. It is read only: choosing, confirming and revoking stay where they are made (the console's Bindings page for its own slots, the Runner's `PUT /api/v1/bindings` and `allow-production-binding` for a runner's), each table enforcing its own binding.

## What was merged

| Before | Now |
| --- | --- |
| `model-slots/1`, `models/_slots.yml`, `SlotBindingsLoader`, the Models tab's "Bind a slot" editor and slots text editor, `PUT /api/agents/models/slots` | `bindings.yml` section `models:`, `BindingsLoader`, the Bindings panel, `PUT /api/bindings/slots/models/<slot>` and `PUT /api/bindings` |
| `delivery.channels` of `settings.yaml`, `ChannelBinding` parsing in `DeliverySettings`, the mapping table of the Delivery tab, `PUT /api/delivery/channels` | `bindings.yml` section `delivery:`; `ChannelBinding` is a view of the shared `SlotBinding` and resolves through it; the same panel; `PUT /api/bindings/slots/delivery/<channel>` |
| runner package slots (a third, separate screen) | read from the runner and shown in the same panel |

Not merged, on purpose: the registry's per-channel `definition_binding` of an agent definition (`Publisher.Bindings(channel, 'agent', name)`, AG13) is the runner's store with another kind (it is the same table); the screen for it is not built. A delivery channel's **limits** stay in `settings.yaml` (`delivery.limits`): a rate is not a binding.
