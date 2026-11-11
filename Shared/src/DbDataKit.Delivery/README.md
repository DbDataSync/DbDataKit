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

## Where this folds into connections later

A channel's endpoint and its secret references are read here by `DeliverySettings` on purpose, as a small reader of their own. When the `connection` format grows a `kind` for outside systems (plan 22a IX01), a delivery channel becomes a connection of kind `mail` or `webhook` and `ChannelConfig` is built from it: the outbox, the sender and the channels' code do not change, only where `ChannelConfig` comes from.

## Opening the module

A host opens `DeliveryModule.Module` (install scope; it depends on `payloads`) with its other modules. Nothing in this slice opens it in a host: the console wires it with the screens (plan 22a GP18).
