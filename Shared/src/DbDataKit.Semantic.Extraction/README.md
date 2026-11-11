# DbDataKit.Semantic.Extraction

`Extractor` (from `Qis.Engine.Extraction`, plan 14 R3): reads a workbook with ClosedXML and an extraction spec (`extraction_spec.schema.json`, embedded in `DbDataKit.Semantic`) and returns the described tables for the engine to register, or errors that name the sheet and cell. Specified in `docs/semantic/extraction.md`; the fixtures are `extraction-fixtures/` of the semantic tests. Saving and versioning shared specs is not built (handoff section 8).
