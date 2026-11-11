# DbDataKit.Semantic.Ssas

`SsasExecutor` (from `Qis.Ssas`, plan 14 R3, J16's library): an `ISourceExecutor` that sends the DAX the engine emits to SQL Server Analysis Services (Tabular) with ADOMD.NET, sends anything else to a fallback executor, and deploys a TMSL model (`DeployModel`, with the refresh that fills a model of `DATATABLE` calculated tables). `Probe` runs one DAX statement and describes the outcome, for settling what a server accepts.

The ADOMD.NET client runs only on Windows (checked: on Linux a connection throws `NotSupportedException`), so a process that uses this executor is a Windows process. The project compiles everywhere. The sample model (`conformance/ssas/model.tmsl.json` of the tests) and how the CI job gets a server are in `docs/semantic/ssas.md` and `docs/semantic/ssas-ci.md`.
