# DbDataKit.Serving.Notify

The producer side of the serving group's result cache (plan 24a UC13, QU8). A product server that finished loading, replicating, running or writing a
dataset tells the serving groups (Gateways) it is paired with, so they clear the cache entries that read it; the Gateway's side is
`POST /api/v1/cache/changed` (see `docs/serving-layer.md`, "The result cache").

- **`DataChangeNotifier`** (`IDataChangeNotifier.Publish(changes)`): sends `data.changed/1` (`DataChangedMessage`: dataset, product, run, at; never row data)
  to every pairing with `only_if_served`, so a Gateway whose lineage graph does not know the dataset does nothing (and says `served: false`) instead of
  clearing its whole cache. With no pairing it sends nothing.
- **Who signs.** A completion has no person, so the server signs as itself: the management token is issued by the key of the server's own pairing with the
  Gateway (the one it publishes with), subject = the server's client id, name `<product> server`, role **Publisher** (no new role). There is no second signature;
  the Gateway's channel (signature, audience, expiry, replay cache) refuses a forged or replayed message.
- **Failure never fails a run.** Delivery is in the background; a call that can pass (Gateway down, timeout, 5xx, 429) is tried once more after
  `RetryAfter` (30 s), a refusal is not retried; either is a log warning and a counter (`Stats`: sent, not served, retried, failed, the last failure).
- **Pairings.** `GatewayPairingsFile` (`gateways.json`, the shape of Build's `proxies.json`) and `GatewayPairingApi` (`GET/POST/DELETE /api/v1/gateways`) for a
  server that pairs only to tell a group of changes (Sync, the Runner). Build and DbDataStore pass their own lists (`IGatewayPairings`).
- **Dataset names** are lineage identities (`LineageIdentity.Dataset(...).ToString()`), the names the Gateway's graph holds.

## The lineage client (plan 21 CH06)

`GatewayLineageClient` reads `GET /api/v1/lineage` from every paired serving group, as the server itself (the same signed token with the Publisher role and the pinned certificate as the notifier; the Gateway lets a Publisher read its graph, without the readers' names). The Store's contract step and Build's apply give the graphs to `ContractGuard.Check` (`DbDataKit.Lineage`) before they drop, rename away or retype a column, which refuses naming each published consumer and its channel. A group that does not answer is in `Unreachable`, never skipped, so the step is refused until a person names it; with no pairing the answer is empty.
