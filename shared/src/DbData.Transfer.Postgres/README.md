# DbData.Transfer.Postgres

`PostgresBulkWriter` (`IBulkWriter`): binary `COPY ... FROM STDIN (FORMAT BINARY)` with an explicit `NpgsqlDbType` per column (`PostgresTypes.For`), rows streamed from the batch readers; values adapted as in `ParameterValues` (unsigned and 8-bit integers widen, so the COPY type matches the value).

`BulkOptions.Transaction`: `None` one COPY (atomic as one statement); `PerBatch` a COPY per `BatchSize` rows, each committed on its own; `Single` the whole load in one transaction opened by the writer. `KeepIdentity`/`KeepNulls` have no equivalent. Binary COPY needs the exact wire type of the destination column: `json` defaults to `jsonb` (pass `typeOverrides` for a `json` column); `string` is written as `text`, which COPY accepts for `varchar`.

Use through `WriteGate.BulkWriteAsync`. Offline tests cover the type map, COPY text and batching rule; live tests (`DBDATA_TEST_POSTGRES`) copy 5,000 rows in each transaction mode and run the conformance cases. Not verified: `interval` and `xml` columns against a real server, `time` precision, throughput versus the old path.
