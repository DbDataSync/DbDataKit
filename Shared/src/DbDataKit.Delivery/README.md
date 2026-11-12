# DbDataKit.Delivery

The delivery service of the suite (plan 22a GP07 to GP09, GP17; plan 22 section 5): a message is written to an **outbox** (the state module `delivery`, `DbDataKit.State`), taken by a sender, handed to a **channel** and its outcome recorded. A report, an alert or a subscription never talks to a mail server: it writes a message and reads what happened to it.

```csharp
var settings = DeliverySettings.Read(section, "settings.yaml");                   // the strict delivery: section
var channels = new DeliveryChannels(settings, connections, DeliveryKinds.Default(), secretResolver, scrubber);   // connections: an IDeliveryConnections
var service  = new DeliveryService(new DeliveryStore(db), new PayloadStore(db), channels, settings.Policy, scrubber);
var id = await service.SendAsync(new DeliveryRequest("mail", "Weekly sales", ["ana@example.com"], Text: "...", Html: "<p>...</p>",
    Link: "https://console.example/views/sales", Attachments: [new("sales.pdf", "application/pdf", bytes)], Source: "subscription:" + sub, OwnerPerson: person));
await service.RunAsync(stopping);        // the sender loop; a runner task calls RunOnceAsync
```

## The rules

- **Taken once.** `DeliveryStore.Claim` takes a message with a guarded update (`row_version`) and a lease; two senders on one database never hold one message; a sender that dies loses it when the lease ends and `Complete` of the dead sender is refused (nothing is recorded twice).
- **Temporary or permanent.** A channel answers `DeliveryResult.Sent / Temporary / Permanent` with the far side's words. Temporary (a timeout, a 5xx, a 429, a full mailbox) is tried again after the policy's back-off (`retry.backoff_seconds`, the last entry repeats) until `retry.max_attempts`, then the message is parked as `dead` ("Gave up after n tries"). Permanent (a refused recipient, a 410) parks it at once. A channel that throws is a temporary failure with outcome `error`. A parked message can be sent again by a person (`DeliveryStore.Requeue`).
- **A recorded outcome per try.** `attempt` has a row for every try (number, sender, outcome, the words), written in the transaction that moves the message.
- **A removed channel** parks what waits for it with a reason that names the channel; sending to a channel that is not configured is refused to the caller (`UnknownChannelException`), so a subscription can report it.
- **A limit per minute** (`limit_per_minute`, per sender process) holds a message back without counting a try.
- **No secret is written.** The channel's address and password are references (`secret:`, `env:`, `file:`) resolved at the moment of the send through `ChannelContext.SecretAsync`, which registers the value with the `SecretScrubber`; the detail of a try and every log line are scrubbed (a server that echoes a rejected password does not put it in a row). Log lines carry ids, channel, sizes and the scrubbed words, never a body. A test sends a canary secret through a channel that echoes it and searches every row and log line.
- **Retention.** A settled message and its attempts go after 30 days (the state default; `retention:` settings change it) and its attachments (payloads owned by the message) with them; pending and sending messages have no age limit.

## Channels are connections (plan 22a GP18 fold)

A channel is a **connection** (`connection/2`, kept by the console as `connections/<name>.yml`): `kind: smtp` for mail, `kind: http` with a `webhook:` section for Slack, Teams and the signed webhook. The `delivery:` section of the install's settings holds only **which connection each delivery channel name uses in each release channel** (plan 21: `prod`, `test`, `dev`, ...), the retry policy and the limits. The library reads connections through `IDeliveryConnections` (the console implements it over its files; a test uses `FixedDeliveryConnections`), so it does not depend on the console.

```yaml
delivery:
  retry:
    max_attempts: 6                       # tries per message
    backoff_seconds: [30, 120, 600, 3600, 21600]
  lease_seconds: 120
  channels:
    mail: smtp-main                       # a name alone binds the production release channel (prod), and only it
    alerts:                               # a release channel to a connection, each explicit
      prod: slack-ops
      test: slack-sandbox
      dev: slack-sandbox
  limits:                                 # optional: messages per minute per delivery channel (else the connection's own limit_per_minute)
    alerts: 30
```

Read strictly (`DeliverySettings.Read`): an unknown key, a duplicate name or a limit for a name that is not a channel fails naming the file and the key. **A channel in the earlier form** (`kind: email`, `host`, `url`, ...) **is refused** naming the file, the key and the new form; there is no migration (the library was never released).

### Release channels: a test never notifies production

`DeliveryChannels.Resolve(name, releaseChannel)` makes the channel from the connection **bound for that release channel**, and never answers with another's: a release channel with no entry has **no channel** (it is not inherited from `prod`: sending is a write, plan 21), so a message written on `test` reaches a sandbox and cannot reach production's Slack because production is bound. `*` binds every release channel that has no entry of its own and is written on purpose. `DeliveryRequest.ReleaseChannel` (null: `prod`) is kept with the message (`message.release_channel`, module migration 3) so a retry goes where the first try went. `SendAsync` refuses a release channel that is not bound (`ChannelNotBoundException`, naming where it is bound) and an unknown name (`UnknownChannelException`); a connection that is missing or does not read is a problem the message reports as a temporary failure (fixing it makes the next try work). A sender holds a limit per minute **per connection**, so a test channel never uses up production's. A channel made from a connection is kept until the connection's file changes; an edited connection is used by the next message.

### Mail (`kind: smtp`)

```yaml
schema: connection/2
kind: smtp
name: smtp-main
smtp:
  host: smtp.example.com               # the certificate must be for this name
  port: 587                            # default: 587 starttls, 465 implicit, 25 none
  tls: starttls                        # starttls (default) | implicit | none (refused unless the host is this machine, or allow_plain_text: true)
  sender: "DbData <noreply@example.com>"
  trusted_certificate: /etc/dbdatakit/mail-ca.pem    # optional: a private CA's file
  limit_per_minute: 30
sign_in: { method: basic, user: robot, password_secret: secret:dbdatakit:server:connection:smtp-main:password }
```

A small SMTP client of the library (`SmtpClientLite`: EHLO, STARTTLS or implicit TLS, AUTH PLAIN or LOGIN, one message per connection) because no mail package is pinned and `System.Net.Mail.SmtpClient` cannot do implicit TLS. The message is multipart (`mixed` with attachments, `alternative` for text and html, UTF-8 in base64; a non-ASCII subject or file name is encoded as the RFCs say; a link is added to both bodies).

- A recipient the server refuses with 5xx parks the message with the server's words and the command it answered; nothing is sent when any recipient is refused (the conversation stops before DATA). 4xx replies, network errors and timeouts are temporary.
- A certificate for another name, or one that chains to no trusted root, is refused (permanent, and it says which); **the name is always checked and there is no setting that turns the check off**. Credentials are never sent before TLS on a `starttls` channel, even when the server does not offer STARTTLS (that is refused too).
- An address that is not one, no recipient, or a line break in the subject is the message's own fault: permanent.

### Slack, Teams and the signed webhook (`kind: http` with `webhook:`)

```yaml
schema: connection/2
kind: http
name: slack-ops
endpoint: https://hooks.slack.com/services/{token}      # the template: the origin and the path are visible, the secret part is a placeholder
endpoint_secrets:
  token: secret:dbdatakit:server:connection:slack-ops:token
webhook:
  format: slack                        # slack (Block Kit) | teams (Adaptive Card) | signed (a JSON document, HMAC-SHA256 and a timestamp)
  allow_private: [erp.internal.example, 10.1.0.0/16]    # optional: hosts, addresses or ranges that may be private
  limit_per_minute: 30                 # optional
  timeout_seconds: 20                  # optional
```

A signed webhook adds `sign_in: { method: signed_webhook, key_secret: secret:..., name: X-DbData-Signature }`. **The address is a template** (`EndpointTemplate`, `DbDataKit.Connections`): a placeholder is in the path or a query value, never in the scheme, host or port, so the private-address check and "what does this connect to" work; the strict reader refuses a literal address with a secret in plain sight (a known service's token, a long random path segment, a `sig=` or `token=` value) and names the part to move. A whole-address secret (`endpoint: secret:...`) is possible for a service nothing classifies and is shown as "address hidden". Teams Workflows is `https://prod-12.westus.logic.azure.com:443/workflows/{flow}/triggers/manual/paths/invoke?api-version=2016-06-01&sig={sig}` with `flow` and `sig` bound.

- **A private address is refused** (loopback, 10/8, 172.16/12, 192.168/16, 169.254/16, fc00::/7, fe80::/10, 100.64/10 and the other special ranges) unless `webhook.allow_private` lists the host name, the address or its CIDR range. It is checked on the address the connection really goes to, so a name that resolves to a private address, or is changed to one between a check and the connect, is refused as well. A plain `http` address is used only for a listed host.
- **No redirect is followed** (it could lead past the check): a 3xx is a permanent failure that says so. The composed address (it carries a token) is never in a message's words: a failure names the host, and **every failure text** (a timeout, a refused redirect, a TLS or DNS error, a body that echoes the address) is scrubbed of the composed address and of each secret part (`WebhookChannel`, a test per failure class that looks in the failure, the outbox rows and the log).
- **Answers:** 2xx is sent; 408, 425, 429 and 5xx are temporary (a 429's `Retry-After` is in the words); a **410 is permanent** and says the webhook was removed; any other 4xx and 3xx is permanent; a network error or a timeout is temporary.
- **Files:** a webhook cannot carry one, so Teams and Slack name the attachments and the link opens what they came from; the signed webhook lists each (name, type, size, SHA-256) and inlines its bytes (`content_base64`) up to 2 MiB.
- **The signature** (`SignedWebhook` in `DbDataKit.Connections.Http`, `WebhookSignature` here): `X-DbData-Signature: t=<unix seconds>,v1=<hex>` (or the header `sign_in.name` says), `v1` = HMAC-SHA256 of `"<t>.<body>"` with the shared secret, the body exactly as sent. A receiver calls `WebhookSignature.Verify(secret, header, body, now, window)`: constant-time comparison, `Mismatch` after one changed byte or another secret, `OutsideWindow` when `t` is more than the window (default five minutes, the receiver's, which is what stops a replay) from its clock, `Malformed`. `X-DbData-Delivery` is the message id (the receiver's idempotency key: a retry carries the same id) and `X-DbData-Attempt` the try.

### Secrets

A connection holds references (`secret:`, `env:`, `file:`, ...), never values; each is resolved at the moment of a send through `ChannelContext.SecretAsync`, which registers the value with the `SecretScrubber`; the detail of a try and every log line are scrubbed. Log lines carry ids, channel, release channel, sizes and the scrubbed words, never a body. A test sends a canary secret through a channel that echoes it and searches every row and log line.

## Subscriptions (plan 22a GP17)

A person subscribes to a dashboard or a report on a schedule: `SubscriptionSpec.Read` reads the request **strictly** (name, kind `dashboard` or `report`, view, the document's parameters by name, the layout it asks for, format `pdf`, `csv` (one `widget`'s rows) or `xlsx`, the delivery channel, an optional recipient, `inline_preview` (PDF only: an email also shows a picture of the document in the message; the picture is an attachment with a `ContentId` that `MailMessageBuilder` writes as `multipart/related`, and Teams, Slack and the webhook get the link only), the schedule as a cron in a time zone; a schedule is at most hourly), `SubscriptionStore` keeps it (`subscription`, `subscription_run` in the `delivery` module, migrations 2, 5 and 6), `SubscriptionService` runs it.

**A process with no channels (the Gateway).** A subscription stores the **kind** of its channel (`channel_kind`: `email`, `teams`, `slack`, `webhook`; migration 6): the console writes it when it saves the subscription (from the channel it has) and its sender keeps it true every minute (`SubscriptionStore.SetChannelKind`, which does not move the row's version, so it also fills a row from before the column and follows a rebinding). The process that draws the document, which has no channels, a connection or a secret (the Gateway), gets its `IDeliveryChannels` from those kinds (`EnqueueOnlyChannels`), so `SubscriptionService` decides what the message holds by kind (an email gets the inline picture, a chat channel the link) and `DeliveryService.SendAsync` writes it to the **shared outbox**; it never runs a sender. The process that has the channels (the console: `ConsoleDeliverySender`) claims the row with its own sender id like any message (a guarded update, so two senders never hold one message), resolves the channel name in the release channel the row names, and keeps the retries, the limit per minute and the dead letters: a name the console no longer has parks the message as `dead` with "is not configured any more", and a person can send it again. A subscription with no stored kind (never saved by a console that had the channel) is not enqueued by such a process: the run is `failed` with the "not configured" words until the console saves or syncs it. The two processes must share the state: a group's SQL Server or PostgreSQL does; two SQLite files do not.

- **As the subscriber.** The service asks an `ISubscriptionRenderer` for the document as the subscriber (their subject id, never the runner's identity); the real one is the Gateway's views module (`DbDataKit.Views.Hosting`, plan 32 DBR18). Two subscribers of one view with different rules get different attachments, and the outbox shows both.
- **A subscriber who lost access** (the renderer says so, or `ISubscriberDirectory` no longer knows them or the identity is disabled) gets nothing: the subscription is `stopped` with the reason in `status_detail`, and a message says why to them and to the owner (who set it up). A person resumes it from the subscriptions page; `RunNowAsync` tries a stopped one to see whether access is back and changes nothing else. **Owner decision 2026-11-11:** the rule is that it does **not deliver**, the run is recorded `access-lost` ("lost access") in the subscription's log, and **an administrator is notified** through the install's notification feed (`INotificationFeed`, kind `SubscriptionAccessLost`, shown on the console; the service takes the feed as `notifications`), besides the message to the subscriber and the owner.
- **A render that fails** (no browser, a deadline) sends nothing, tells the owner, and the subscription stays active for its next time.
- **A removed channel** is a problem the subscription shows (`status_detail`, outcome `failed`); a good run clears it.
- **The schedule is the runner's.** `ClaimDue` takes the active subscriptions whose `next_run_at` has come and moves each to its next time by a guarded update (two runners take a subscription once; a runner that was down takes it once, not once per missed time). The cron is read in the subscription's time zone, so 07:00 stays 07:00 across a daylight change (Berlin 06:00 UTC before 2026-03-29, 05:00 after; New York 12:00 UTC before 2026-03-08, 11:00 after). The runner task that calls `RunDueAsync` every minute is `views-export` (plan 32 DBR18).
- The runs of a subscription are kept 90 days (`subscription_run`); the subscription itself goes only when a person removes it.

## Alerts (plan 22a GP16)

`AlertService` turns what `IAlertSource`s say is wrong into messages by the rules of `AlertStore` (`alert_rule`, `alert_state` of the `delivery` module, migration 4). The kinds are fixed (`AlertKinds`: `failed-run`, `late-data`, `approvals-waiting`, `expiring-preview`); a kind is **off until an administrator turns it on** (nothing is sent or remembered for a kind that is off), and a rule names a delivery channel, optional addresses, a cool-down and a release channel.

- **Once, then after the cool-down.** A source is told when it starts failing; while it keeps failing it stays quiet until the cool-down has passed, then it is told again. The cool-down is per source **and recipient** (`alert_state`), so adding a recipient tells only that one.
- **One "recovered".** A source that was told and is not failing any more gets one `Recovered` message per recipient and its row says `ok`; later passes say nothing.
- **No channel: the feed only.** A rule with no channel raises the alert in the notification feed (`Alert`, `AlertRecovered`) and sends no message; a told alert is raised in the feed in either case.
- **A source that cannot be read** ends the pass with the reason (`AlertRunResult.Problems`) and tells nothing as recovered; a channel that is not configured or not bound is a problem of the pass and nothing is remembered, so the next pass tries again.
- **Sources.** `HealthAlertSource` maps `HealthEvaluator` findings (failed run; overdue, lagging and failed checks as late data); `RunHistoryAlertSource` reads the last run of each job in the `runs` module. Approvals and previews have no source yet: a host registers its own `IAlertSource`.
- The console runs the pass every minute (`ConsoleAlertSender`) and edits the rules on the Delivery page, tab Alerts (`/api/delivery/alerts`).

## Opening the module

A host opens `DeliveryModule.Module` (install scope; it depends on `payloads`) with its other modules. The console does (`ConsoleState.Modules`): it reads the `delivery:` section of `settings.yaml` in its directory (which connection each channel name uses in each release channel; the connections are its own `connections/<name>.yml`), runs the sender as a hosted service and serves `/api/delivery` and the Delivery page (channels with a test send, subscriptions, the outbox). The console cannot draw a document, so a subscription is rendered by the process that runs the views module with the `views-export` task (`DbDataKit.Delivery.Runner`), over the same state (the Gateway's `GatewaySubscriptions` does, when it can export and has delivery channels registered); the console's "send now" is offered only when a process registers an `ISubscriptionRenderer`.
