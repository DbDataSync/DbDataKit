# Channel bindings

One concept for every place the suite binds a **name** to a **connection** per **release channel** (owner direction, 2026-11-11: the suite has no two designs, formats, screens or pieces of code for the same thing). Plan 27a IX23 built it; plan 22a GP18 (decision D10) and plan 27a AG05 and AG18 are its first users.

## The concept

A **binding** is: a **slot** of a **kind**, and for each release channel a **connection** (for a model, which of its models), or for a model slot an **ordered list** of them (the first is used, the others take over when it is down).

| Kind | The slot is | A release channel takes | Kept in |
| --- | --- | --- | --- |
| `model-chat` | the name an agent asks for (`default`, `review`) | a list of `{connection, model}` of a connection of kind `model-provider`, purpose `chat` | `bindings.yml`, section `models:` |
| `model-embeddings` | the name `embeddings` (a channel's knowledge index) | exactly one `{connection, model}` of a provider with purpose `embeddings` (an index has one model) | `bindings.yml`, section `models:` |
| `delivery` | a delivery channel's name (`alerts`, `mail`) | exactly one connection that can send: `kind: smtp`, or `kind: http` with a `webhook:` section | `bindings.yml`, section `delivery:` |
| `runner` | a connection slot of a runner package (`package/1`, frozen), named `package/slot` | one connection (a name or a secret reference) | the runner's own store (`definition_binding`); **only shown** here, in the same shape |

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

For a slot that sends or writes (a delivery channel, a runner slot), a **non-production release channel that uses a connection production uses** is an explicit, confirmed, recorded choice: a test message that reaches production's Slack is what the rule exists to prevent. A model slot only reads (a prompt leaves; nothing in the world changes; what may leave is the agent policy's rule), so it needs no confirmation.

* The console refuses a save that would make one (HTTP 409 with `needsConfirmation`, each item naming the slot, the release channel, the connection and the effect) until it is confirmed, then records it as the posture choice `production_binding_on_non_production` (the catalogue entry of `PostureSetting`), scoped `bindings/<section>/<slot>/<release channel>` (who, when, a note), in `bindings-posture.json` beside `bindings.yml` and in the audit log (`bindings.posture.choose` and `.revoke`). It is a file, not the runner's `posture_choice` table, because the console's state has no `runner` module unless the embedded runner is on; the file is written by the confirmation and is not edited by hand.
* Moving the slot to its own (sandbox) connection takes the choice back.
* `*` with no `prod` entry binds production's connection for every other release channel and is the same case, scoped `.../*`.
* What is **not** built: a hand-edited `bindings.yml` is checked by the screen (it shows "nobody confirmed it") but not refused at send time; the runner's own binding store does not take the choice (it binds by its own API).

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

**Bindings** (`/bindings`, `Shared/web/console/src/pages/BindingsPage.tsx`) lists every slot of every kind with, per release channel, the connection (and model), whether it can be used now, and for an administrator **Test**, **Change** (a connection per release channel, the fallback order, a release channel added or removed), **Remove the slot**, and the confirmation of the IX17 choice. The Models tab of Agents and the Channels tab of Delivery show **the same panel** for their kinds (no second editor); the file itself can be edited as text on the same page. The runner's package slots are read from the runner (`/relay/console-runner/api/v1/definitions/integration`) and shown in the same shape with the runner's own Test; to change them, the runner's own screens and API stay (its store and `package/1` are frozen).

## The runner: what it keeps and how the console hands it a connection (design; not built)

The Runner is a separate server and **does not read the console's `connections/` folder**. Today a runner slot is bound to a name the runner knows (`connections/<name>.yaml` on the runner, or a secret reference), and that stays true for database slots.

For a slot that needs a connection of the console (an HTTP API, a webhook, a mail server for a task that sends), the hand-over, inside this concept, is:

1. The binding to a runner slot names a console connection. When the administrator binds it on the Bindings page, the console sends the runner **the connection definition with its secret references, never a value** (`PUT /api/v1/connections/<name>` through the existing runner relay, an administrator's call, audited with the name only).
2. The runner keeps that definition **in its own store** (a `connection` row beside `definition_binding`), versioned; the binding stays what `package/1` and the binding store say today (`slot -> name`), so nothing frozen changes.
3. A secret reference resolves **on the runner**, in the runner's encrypted store, the environment or the credentials directory, as every login does (`DbDataKit.Secrets`); the console sends the value separately with `secret send --to runner` (plan 28c E20d). A reference the runner cannot resolve is the existing failure naming the slot, the connection and the reference.
4. `http` slots resolve from that runner-side copy: the slot resolver finds the definition in the store and creates the client with `DbDataKit.Connections.Http`.
5. Changing the console's connection does **not** change the runner's copy until it is sent again; the screen shows the copy's version against the console's and offers "send again". That is the same rule as a package: what runs is what was published.

Built today: the screen reads and tests the runner's slots in the shared shape. Not built: the runner-side store and route (step 1 to 5), the "send again" state, and the posture choice for runner slots.

## What was merged

| Before | Now |
| --- | --- |
| `model-slots/1`, `models/_slots.yml`, `SlotBindingsLoader`, the Models tab's "Bind a slot" editor and slots text editor, `PUT /api/agents/models/slots` | `bindings.yml` section `models:`, `BindingsLoader`, the Bindings panel, `PUT /api/bindings/slots/models/<slot>` and `PUT /api/bindings` |
| `delivery.channels` of `settings.yaml`, `ChannelBinding` parsing in `DeliverySettings`, the mapping table of the Delivery tab, `PUT /api/delivery/channels` | `bindings.yml` section `delivery:`; `ChannelBinding` is a view of the shared `SlotBinding` and resolves through it; the same panel; `PUT /api/bindings/slots/delivery/<channel>` |
| runner package slots (a third, separate screen) | read from the runner and shown in the same panel |

Not merged, on purpose: the registry's per-channel `definition_binding` of an agent definition (`Publisher.Bindings(channel, 'agent', name)`, AG13) is the runner's store with another kind (it is the same table); the screen for it is not built. A delivery channel's **limits** stay in `settings.yaml` (`delivery.limits`): a rate is not a binding.
