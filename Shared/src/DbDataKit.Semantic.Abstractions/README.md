# DbDataKit.Semantic.Abstractions

The contracts of the semantic layer (plan 14, from `Qis.Contracts`): `SemanticRequest` and its `Filter`, `MeasureRef` and `TransformRef`, the `Result` with its `ResultSet`s, `QisError`, `NativeQuery`, `ResolvedRange`, the JSON options (`QisJson.Options`, `QisJson.RequestHash`) and the executor interface (`ISourceExecutor`, `CompiledQuery`, `QueryRows`).

No engine and no drivers, so a host, a client or a test can reference it alone. The engine is `DbDataKit.Semantic`.
