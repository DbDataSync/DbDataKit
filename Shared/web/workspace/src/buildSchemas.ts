import { SchemaSet, schemaFor, type SchemaAssociation, type SchemaNode } from '@dbdatakit/shell'

/**
 * Build's own schemas for the files of a project (`DbDataBuild/schemas/*.schema.json`, the ones its management API serves to its screens), read into the build as JSON so the Workspace's forms and Build's never describe a file differently (plan 33 SC20m).
 * The Workspace holds no copy: a schema Build changes is the schema the form draws. `output.schema.json` is the command output's, not a file's.
 */
const found = import.meta.glob(['../../../../DbDataBuild/schemas/*.schema.json', '!../../../../DbDataBuild/schemas/output.schema.json'], { import: 'default', eager: true }) as Record<string, SchemaNode>

/** The shared `check/1` schema the semantic file's `checks` refer to by its `$id` (a library's, `DbDataKit.Quality`): the set has to hold it under that name. */
const quality = import.meta.glob('../../../../Shared/src/DbDataKit.Quality/schemas/check.schema.json', { import: 'default', eager: true }) as Record<string, SchemaNode>

/** The semantic library's schemas that the semantic file's request parts refer to by file name (`semantic_request.schema.json` and what it refers to). */
const semantic = import.meta.glob('../../../../Shared/src/DbDataKit.Semantic/schemas/*.schema.json', { import: 'default', eager: true }) as Record<string, SchemaNode>

export const BUILD_SCHEMAS: Record<string, SchemaNode> = {
  ...Object.fromEntries(Object.entries(semantic).map(([path, schema]) => [path.slice(path.lastIndexOf('/') + 1), schema])),
  ...Object.fromEntries(Object.entries(found).map(([path, schema]) => [path.slice(path.lastIndexOf('/') + 1), schema])),
  ...Object.fromEntries(Object.values(quality).map((schema) => [String(schema.$id), schema])),
}

/** Which schema a file of a project has: the same table as `FileSchemas.All` of the Build server (a test keeps the two equal). */
export const BUILD_ASSOCIATIONS: readonly SchemaAssociation[] = [
  { pattern: 'models/**/_dbdatabuild.yml', schema: 'folder.schema.json', kind: 'folder settings' },
  { pattern: 'models/**/*.yml', schema: 'model.schema.json', kind: 'model' },
  { pattern: 'tests/models/**/*.yml', schema: 'model-test.schema.json', kind: 'model test' },
  { pattern: 'semantic/**/*.yml', schema: 'semantic.schema.json', kind: 'semantic model' },
  { pattern: 'dbdatabuild.yml', schema: 'config.schema.json', kind: 'project' },
  { pattern: 'connections/*.yaml', schema: 'connection.schema.json', kind: 'connection' },
  { pattern: 'answers.yml', schema: 'answers.schema.json', kind: 'answers' },
]

export const buildSchemaSet = new SchemaSet(BUILD_SCHEMAS)

/** The schema and the name of what the file is, or undefined for a file the project has no schema for. */
export const buildSchemaOf = (path: string): SchemaAssociation | undefined => schemaFor(BUILD_ASSOCIATIONS, path)

export type ModelLayout = 'nested' | 'dotted'

/** The project's layout of model files, read from the paths of the draft: `models/<schema>.<object>.sql` is dotted, anything else is a folder per schema. */
export function modelLayoutOf(paths: readonly string[]): ModelLayout {
  return paths.some((p) => /^models\/[^/]+\.[^/]+\.sql$/.test(p)) ? 'dotted' : 'nested'
}

/** Where `dbdatabuild project model create` puts a model's two files, by the layout of the project. */
export function modelFilesOf(name: string, layout: ModelLayout): { yml: string; sql: string } {
  const stem = layout === 'dotted' ? `models/${name}` : `models/${name.replace('.', '/')}`
  return { yml: `${stem}.yml`, sql: `${stem}.sql` }
}

/** Why a new model's files cannot be made: nothing while the name is empty, a sentence when a file of the model is already in the draft. */
export function newModelClash(name: string, layout: ModelLayout, paths: readonly string[]): string | null {
  if (name === '') return null
  const { yml, sql } = modelFilesOf(name, layout)
  const clash = [yml, sql].find((f) => paths.includes(f))
  return clash ? `${clash} exists already: ${name} is a model of this draft (or its file is in the way).` : null
}

/**
 * The two files `dbdatabuild project model create <name> --kind <kind>` writes (`ModelCreateCommand.cs`): a definition with the one column the placeholder query returns, and the query. The tool's text is the model of this one, and
 * the Check tab runs the tool's own reader over what is made, so a difference shows as a finding and not as a silent change.
 */
export function newModelFiles(name: string, kind: string, connections: readonly string[], layout: ModelLayout): { path: string; text: string }[] {
  const { yml, sql } = modelFilesOf(name, layout)
  const obj = name.slice(name.indexOf('.') + 1)
  const table = kind !== 'view'
  const timeRange = kind === 'incremental_by_time_range'
  const kindText = kind === 'incremental_by_unique_key' ? 'kind: {incremental_by_unique_key: {unique_key: [id]}}' : timeRange ? 'kind: {incremental_by_time_range: {time_column: event_ts}}' : `kind: ${kind}`
  const def = [`name: ${name}`, kindText]
  if (table) def.push('grain: [id]')
  if (connections.length > 0) def.push(`connections: [${connections.join(', ')}]`)
  def.push('columns:', '  - {name: id, type: BIGINT, nullable: false}')
  if (timeRange) def.push('  - {name: event_ts, type: TIMESTAMP, nullable: false}')
  if (kind === 'incremental_by_unique_key') def.push('indexes:', `  - {name: ux_${obj}_id, columns: [id], unique: true}`)
  const query = [
    "-- Replace this with the model's query, in DuckDB's dialect. Then `dbdatabuild project model update` brings the definition's columns in line with it.",
    timeRange ? "SELECT CAST(1 AS BIGINT) AS id, TIMESTAMP '2000-01-01 00:00:00' AS event_ts" : 'SELECT CAST(1 AS BIGINT) AS id',
  ]
  return [{ path: yml, text: def.join('\n') + '\n' }, { path: sql, text: query.join('\n') + '\n' }]
}
