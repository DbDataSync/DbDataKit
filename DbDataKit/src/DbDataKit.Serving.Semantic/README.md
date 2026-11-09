# DbDataKit.Serving.Semantic

The semantic module of the serving layer (plan 19, J10; plan 14 R2). A host that already serves SQL (`IServingServices`) gets semantic queries by wrapping its services:

```csharp
var models = new SemanticModels().Add(Catalog.Load("sales.yaml"), sources: new() { ["warehouse"] = "dw", ["budget"] = "dw" });
IServingServices services = hostServices.WithSemantic(models);
```

- **A model is a source.** A request whose source is `semantic:<model>` is a semantic query: its text is the JSON request (a text that starts with `{`) or semantic SQL (`SELECT ... MEASURE(...) FROM <model>`). `ServingRequest.Semantic` and `ServingRequest.SemanticSql` are answered the same way. Anything else goes to the host unchanged.
- **The host runs the data.** The engine plans one scan per catalog source; each is sent to the host's own query service as SQL, as the caller, against the serving source the model maps that catalog source to. The host's access rules, read-only classifier, limits and failure model apply to it; nothing here opens a connection.
- **The answer is an Arrow table** of the combined result, with the engine's status, request hash, level, warnings and remaining steps in the schema's metadata (`qis.*`). A request the engine refuses fails with `ServingInvalidException` naming the error codes.
- **The catalog describes the model**: under `semantic:<model>` the dimensions, their attributes and the measures are listed as `Model`, `Dimension` and `Measure` objects.

- **Other statements on a model source:** `{"kind":"describe"}` (dimensions, attributes, measures, saved queries), `{"kind":"members"}` (the distinct values of an attribute, under the caller's access; `search`, `limit`) and `EXPLAIN`; there is no custom Flight action.
- **Parameters and prepared statements** (plan 19 J25): `{"param": ...}` in a JSON request, `?`, `$n`, `:name` and `IN :list` in semantic SQL; a prepared statement on a model answers its parameter schema (a field per parameter, `qis.for` in the metadata, lists as Arrow lists) and executes with the bound values; the rows and the request hash equal the literal form's.
- **Saved queries** (J26, J27): a catalog's `queries` and the caller's own or shared ones (`IUserSavedQueries`, the Gateway keeps them in its state database) are called as `SELECT * FROM name(args)`, run as the caller.
- **SSAS** sources run in the executor `DbDataKit.Semantic.Ssas` on Windows (the Gateway hosts it); other systems answer `dax_execution_unavailable`.

Not here: ad hoc tables (registrations, bindings, value maps) and the request kinds that need them, decimals in the answer (a number is an Arrow double), and a child process for the combine (limits in process first: J11).

## Explain

`EXPLAIN <semantic SQL>`, or a JSON request with `"kind": "explain"` in place of `query`, answers what the request would run: one row per scan with `source`, `engine` and `text`, and nothing is sent to a source.
