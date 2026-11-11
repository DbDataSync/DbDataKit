# DbDataKit.Delivery

The delivery service of the suite (plan 22a GP07 to GP09, GP17; plan 22 section 5): a message is written to an **outbox** (the state module `delivery`, `DbDataKit.State`), taken by a sender, handed to a **channel** and its outcome recorded. A report, an alert or a subscription never talks to a mail server: it writes a message and reads what happened to it.

```csharp
var settings = DeliverySettings.Read(section, "server.yaml", kinds);          // the strict delivery: section
var channels = new DeliveryChannels(settings, kinds, secretResolver, scrubber);
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

## The `delivery:` section

```yaml
delivery:
  retry:
    max_attempts: 6                       # tries per message
    backoff_seconds: [30, 120, 600, 3600, 21600]
  lease_seconds: 120
  channels:
    mail:                                 # the channel's name
      kind: email
      limit_per_minute: 30                # optional, every kind
      # the kind's own keys: see its section below
```

Read strictly (`DeliverySettings.Read`): an unknown key, an unknown kind, a duplicate name or a secret written out instead of referenced fails naming the file and the key. The channel kinds register with `ChannelKinds` (a kind lists its keys and their types; a `Secret` key takes only a reference).

## The email channel (`kind: email`)

```yaml
    mail:
      kind: email
      host: smtp.example.com               # the certificate must be for this name
      port: 587                            # default: 587 starttls, 465 implicit, 25 none
      tls: starttls                        # starttls (default) | implicit | none
      from: "DbData <noreply@example.com>"
      username: robot
      password: secret:dbdata/delivery/mail/password
      helo: dbdatakit                      # optional
      timeout_seconds: 30                  # optional
      trusted_certificate: /etc/dbdatakit/mail-ca.pem   # optional: a private CA's file
      limit_per_minute: 30
```

A small SMTP client of the library (`SmtpClientLite`: EHLO, STARTTLS or implicit TLS, AUTH PLAIN or LOGIN, one message per connection) because no mail package is pinned and `System.Net.Mail.SmtpClient` cannot do implicit TLS. The message is multipart (`mixed` with attachments, `alternative` for text and html, UTF-8 in base64; a non-ASCII subject or file name is encoded as the RFCs say; a link is added to both bodies).

- A recipient the server refuses with 5xx parks the message with the server's words and the command it answered; nothing is sent when any recipient is refused (the conversation stops before DATA). 4xx replies, network errors and timeouts are temporary.
- A certificate for another name, or one that chains to no trusted root, is refused (permanent, and it says which); **the name is always checked and there is no setting that turns the check off**. Credentials are never sent before TLS on a `starttls` channel, even when the server does not offer STARTTLS (that is refused too).
- An address that is not one, no recipient, or a line break in the subject is the message's own fault: permanent.

## Teams, Slack and the signed webhook (`kind: teams | slack | webhook`)

```yaml
    ops-teams:
      kind: teams                          # an Adaptive Card in the envelope an incoming webhook takes
      url: secret:dbdata/delivery/ops-teams/url       # the address carries a token: always a reference
    ops-slack:
      kind: slack                          # Block Kit: header, section (mrkdwn, escaped), a button for the link
      url: secret:dbdata/delivery/ops-slack/url
    erp-hook:
      kind: webhook                        # a JSON document signed with HMAC-SHA256 and a timestamp
      url: env:ERP_HOOK_URL
      secret: secret:dbdata/delivery/erp-hook/signing
      allow_private: [erp.internal.example, 10.1.0.0/16]   # optional, every webhook kind
      timeout_seconds: 20
```

- **A private address is refused** (loopback, 10/8, 172.16/12, 192.168/16, 169.254/16, fc00::/7, fe80::/10, 100.64/10 and the other special ranges) unless `allow_private` lists the host name, the address or its CIDR range. It is checked on the address the connection really goes to, so a name that resolves to a private address, or is changed to one between a check and the connect, is refused as well. A plain `http` address is used only for a listed host.
- **No redirect is followed** (it could lead past the check): a 3xx is a permanent failure that says so. The address is never in a message's words (it carries a token); a failure names the host.
- **Answers:** 2xx is sent; 408, 425, 429 and 5xx are temporary (a 429's `Retry-After` is in the words); a **410 is permanent** and says the webhook was removed; any other 4xx and 3xx is permanent; a network error or a timeout is temporary.
- **Files:** a webhook cannot carry one, so Teams and Slack name the attachments and the link opens what they came from; the signed webhook lists each (name, type, size, SHA-256) and inlines its bytes (`content_base64`) up to 2 MiB.
- **The signature** (`WebhookSignature`): `X-DbData-Signature: t=<unix seconds>,v1=<hex>`, `v1` = HMAC-SHA256 of `"<t>.<body>"` with the shared secret, the body exactly as sent. A receiver calls `WebhookSignature.Verify(secret, header, body, now, window)`: constant-time comparison, `Mismatch` after one changed byte or another secret, `OutsideWindow` when `t` is more than the window (default five minutes) from its clock (this is what stops a replay), `Malformed`. `X-DbData-Delivery` is the message id (the receiver's idempotency key: a retry carries the same id) and `X-DbData-Attempt` the try.

## Subscriptions (plan 22a GP17)

A person subscribes to a dashboard or a report on a schedule: `SubscriptionSpec.Read` reads the request **strictly** (name, kind `dashboard` or `report`, view, the document's parameters by name, the layout it asks for, format `pdf`, `csv` (one `widget`'s rows) or `xlsx`, the delivery channel, an optional recipient, the schedule as a cron in a time zone; a schedule is at most hourly), `SubscriptionStore` keeps it (`subscription`, `subscription_run` in the `delivery` module, migration 2), `SubscriptionService` runs it.

- **As the subscriber.** The service asks an `ISubscriptionRenderer` for the document as the subscriber (their subject id, never the runner's identity); the real one is the Gateway's views module (`DbDataKit.Views.Hosting`, plan 32 DBR18). Two subscribers of one view with different rules get different attachments, and the outbox shows both.
- **A subscriber who lost access** (the renderer says so, or `ISubscriberDirectory` no longer knows them or the identity is disabled) gets nothing: the subscription is `stopped` with the reason in `status_detail`, and a message says why to them and to the owner (who set it up). A person resumes it from the subscriptions page; `RunNowAsync` tries a stopped one to see whether access is back and changes nothing else.
- **A render that fails** (no browser, a deadline) sends nothing, tells the owner, and the subscription stays active for its next time.
- **A removed channel** is a problem the subscription shows (`status_detail`, outcome `failed`); a good run clears it.
- **The schedule is the runner's.** `ClaimDue` takes the active subscriptions whose `next_run_at` has come and moves each to its next time by a guarded update (two runners take a subscription once; a runner that was down takes it once, not once per missed time). The cron is read in the subscription's time zone, so 07:00 stays 07:00 across a daylight change (Berlin 06:00 UTC before 2026-03-29, 05:00 after; New York 12:00 UTC before 2026-03-08, 11:00 after). The runner task that calls `RunDueAsync` every minute is `views-export` (plan 32 DBR18).
- The runs of a subscription are kept 90 days (`subscription_run`); the subscription itself goes only when a person removes it.

## Where this folds into connections later

A channel's endpoint and its secret references are read here by `DeliverySettings` on purpose, as a small reader of their own. When the `connection` format grows a `kind` for outside systems (plan 22a IX01), a delivery channel becomes a connection of kind `mail` or `webhook` and `ChannelConfig` is built from it: the outbox, the sender and the channels' code do not change, only where `ChannelConfig` comes from.

## Opening the module

A host opens `DeliveryModule.Module` (install scope; it depends on `payloads`) with its other modules. Nothing in this slice opens it in a host: the console wires it with the screens (plan 22a GP18).
