import type { SchemaNode } from '@dbdatakit/shell'

/**
 * The shared `check/1` schema of `DbDataKit.Quality` (`Shared/src/DbDataKit.Quality/schemas/check.schema.json`), read into the build as JSON: the seven kinds of data quality check that a semantic file, a Store model and a replication's `checks:` section
 * all carry. The Workspace holds no copy; its forms refer to the schema by its `$id`, as the semantic schema does.
 */
const found = import.meta.glob('../../../../Shared/src/DbDataKit.Quality/schemas/check.schema.json', { import: 'default', eager: true }) as Record<string, SchemaNode>

export const CHECK_SCHEMA: SchemaNode = Object.values(found)[0]!
export const CHECK_SCHEMA_ID = String(CHECK_SCHEMA.$id)

/** A reference to the section of checks (a list of `check/1` entries) in a form's schema. */
export const CHECKS_REF = `${CHECK_SCHEMA_ID}#/$defs/checks`
