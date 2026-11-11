# DbDataKit.Runner.Kit.Landing

WIP (plan 27a IX11): rows into a staging table of a suite connection through `DbDataKit.Transfer`. `StagingLander` (write gate, one transaction per batch, an idempotency key written into every row so a batch lands once; `Replace` lands it anew), `ValueCoercion` (text and file values to the column's type, never rounded or guessed), bad records by the decision of `DbDataKit.Runner.Kit.Formats`. Not yet compiled against its tests; rows to DbDataStore are not built because no client library of the Store's API exists.
