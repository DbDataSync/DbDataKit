import { parseYaml, setAt, type SchemaNode } from '@dbdatakit/shell'
import { referenceProblem } from './model'

/** The names the forms know the Sync schemas by. */
export const SYNC_TASK_SCHEMA = 'sync-task.schema.json'
export const SYNC_MAPPING_SCHEMA = 'sync-mapping.schema.json'
export const SYNC_CONNECTION_SCHEMA = 'sync-connection.schema.json'

/** The format a `task.yaml` is written in now (`ConfigSchema.TaskCurrent`); Sync reads this and the one before it and every save writes this one (plan 33 section 6d). */
export const SYNC_TASK_CURRENT = 2

const endpoint: SchemaNode = {
  type: 'object',
  properties: {
    connectionName: { description: 'The name of a connection of this set (config/connections/<name>.yaml).', type: 'string' },
    database: { description: 'The database on that connection.', type: 'string' },
  },
}

const kindOptions = (what: string): SchemaNode => ({
  type: 'object',
  description: what,
  properties: { kind: { description: 'Which implementation does it (the drivers Sync knows are checked when the draft is).', type: 'string' }, options: { description: 'Its settings, by name.', type: 'object', additionalProperties: true } },
})

const scheduling: SchemaNode = {
  type: 'object',
  description: 'When it runs: a continuous loop with a frequency, or periodically on a cron schedule. Exactly one of `frequencySeconds` and `cronExpression` fits the mode.',
  required: ['mode'],
  properties: {
    mode: { description: 'Continuous loops all the time; Periodic runs on the cron schedule.', enum: ['Continuous', 'Periodic'] },
    frequencySeconds: { description: 'Continuous only: seconds between two passes.', type: 'integer', minimum: 1 },
    cronExpression: { description: 'Periodic only: five fields, minute first (for example `*/15 * * * *`).', type: 'string' },
    idleTimeoutSeconds: { description: 'How long it waits when there was nothing to do.', type: 'integer', minimum: 1 },
  },
}

/**
 * The shape of a replication's `task.yaml`, for the form (`dbdatasync`'s strict readers are the authority and their words are what the Check shows: this schema only says which settings exist so the form can offer them, and keeps no rule the readers do not have).
 * The parts that are open sets of names (scripts, hooks, provisioning, segmenting, checks) are left to the YAML tab.
 */
export const syncTaskSchema: SchemaNode = {
  $id: SYNC_TASK_SCHEMA,
  type: 'object',
  description: 'A Sync replication: when it runs, how changes are read, kept and written, and the reload used for a bulk load.',
  required: ['schemaVersion', 'name', 'scheduling', 'changeProcessing'],
  properties: {
    schemaVersion: { description: 'The format version of the file; Sync reads 1 and 2 and the form writes 2.', enum: [1, 2] },
    name: { description: 'The replication\'s name; the same as its folder.', type: 'string' },
    enabled: { description: 'A disabled replication is kept but does not run.', type: 'boolean' },
    notes: { description: 'Free text for the people who maintain it.', type: 'string' },
    scheduling,
    changeProcessing: {
      type: 'object',
      description: 'How a change is read from the source, kept between the two and written to the target.',
      required: ['reader', 'cache', 'writer'],
      properties: { reader: kindOptions('Reads the changes of the source.'), cache: kindOptions('Keeps the changes between the read and the write.'), writer: kindOptions('Writes them to the target.') },
    },
    bulkLoad: { type: 'object', description: 'The full reload used for the first load or a repair.', properties: { reader: kindOptions('Reads the whole source.') } },
    endpoints: { type: 'object', description: 'The default source and target connection of the replication\'s mappings.', properties: { source: endpoint, target: endpoint } },
    reconcile: {
      type: 'object',
      description: 'The periodic comparison of target and source.',
      properties: {
        enabled: { type: 'boolean' },
        afterChange: { type: 'object', description: 'What is compared right after a change run.', properties: { mode: { description: 'none, or the name of a strategy.', type: 'string' } } },
        deleteGuard: { type: 'object', description: 'Refuses a run that would delete more than the ratio of the target\'s rows.', properties: { mode: { enum: ['none', 'ratio'] }, maxRatio: { type: 'number', minimum: 0, maximum: 1 } } },
      },
    },
  },
}

const table: SchemaNode = {
  type: 'object',
  properties: {
    connectionName: { description: 'The name of a connection of this set.', type: 'string' },
    database: { type: 'string' },
    schema: { type: 'string' },
    table: { type: 'string' },
    filter: { description: 'A condition on the source rows.', type: 'string' },
    query: { description: 'A query instead of a table (a source only).', type: 'string' },
    allowSubquery: { type: 'boolean' },
  },
}

/** The shape of a table mapping of a replication (`table-mappings/<name>.yaml`): its tables, its column mappings and primary key. The column cache (`sourceColumns`, `targetColumns`) is read from the database where Sync has one and is edited in the YAML tab. */
export const syncMappingSchema: SchemaNode = {
  $id: SYNC_MAPPING_SCHEMA,
  type: 'object',
  description: 'A table mapping: which source tables feed which target tables and how the columns correspond.',
  required: ['schemaVersion', 'name', 'sources', 'targets'],
  properties: {
    schemaVersion: { description: 'The format version of the file.', enum: [1] },
    name: { type: 'string' },
    sources: { type: 'array', description: 'The source tables.', items: table, minItems: 1 },
    targets: { type: 'array', description: 'The target tables.', items: table, minItems: 1 },
    columnMappings: {
      type: 'array',
      description: 'Columns that do not simply correspond by name.',
      items: { type: 'object', required: ['sourceColumn', 'targetColumn'], properties: { sourceColumn: { type: 'string' }, targetColumn: { type: 'string' }, transform: { description: 'An expression applied to the source value.', type: 'string' }, targetType: { type: 'string' } } },
    },
    primaryKey: { type: 'array', description: 'The target columns that identify a row.', items: { type: 'string' } },
  },
}

/** The shape of a connection file (`connection/1`) of the set: where, as whom and by which secret. It names the secret and never holds a login's password. */
export const syncConnectionSchema: SchemaNode = {
  $id: SYNC_CONNECTION_SCHEMA,
  type: 'object',
  description: 'A connection of the set. The password is not here: the file names the secret that holds it, and the value is set on the server that runs the replication.',
  required: ['schema', 'name', 'engine'],
  properties: {
    schema: { description: 'The format of the file.', enum: ['connection/1', 'connection/2'] },
    name: { description: 'The connection\'s name; the same as the file\'s.', type: 'string' },
    engine: { description: 'The driver: sqlserver, postgres, mysql, oracle, duckdb or an Arrow Flight SQL variant.', type: 'string' },
    address: { type: 'object', description: 'Where the server is.', properties: { host: { type: 'string' }, port: { type: 'integer' }, database: { type: 'string' } } },
    auth: {
      type: 'object',
      description: 'Who it signs in as.',
      properties: {
        mode: { description: 'sql signs in with a login and a password; windows with the service\'s own account.', type: 'string' },
        user: { type: 'string' },
        password_secret: { description: 'A reference to the secret that holds the password: secret:dbdata:sync:connection:<name>. Never the password.', type: 'string' },
      },
    },
  },
}

export const syncSchemaOf = (kind: 'sync-task' | 'sync-mapping' | 'sync-connection') => (kind === 'sync-task' ? SYNC_TASK_SCHEMA : kind === 'sync-mapping' ? SYNC_MAPPING_SCHEMA : SYNC_CONNECTION_SCHEMA)

/** What a form edit of a `task.yaml` writes: the file in the current format whatever it was read as (a file read as 1 and saved by an editor is 2), and nothing else changed. Text that is not YAML is returned as it is. */
export function withCurrentTaskVersion(text: string): string {
  const parsed = parseYaml(text)
  if (parsed.errors.length > 0 || typeof parsed.value !== 'object' || parsed.value === null) return text
  const version = (parsed.value as { schemaVersion?: unknown }).schemaVersion
  if (typeof version !== 'number' || version === SYNC_TASK_CURRENT) return text
  return setAt(text, ['schemaVersion'], SYNC_TASK_CURRENT)
}

/** A place in a connection's text where a secret would reach git, and what to do about it (the server's check says the same words when the draft is checked). */
export interface SecretFinding { line: number; message: string }

/** Looks at a connection file's text for a password where a reference belongs: a value under `password`, a `password_secret` that is not a reference, a password in a connection string. The same three ways the Workspace server refuses. */
export function secretFindings(text: string): SecretFinding[] {
  const found: SecretFinding[] = []
  text.replace(/\r\n/g, '\n').split('\n').forEach((line, i) => {
    if (line.trimStart().startsWith('#')) return
    if (/^\s*password\s*:\s*\S/i.test(line)) { found.push({ line: i + 1, message: 'holds a password value under `password`. Name a secret with `password_secret: secret:dbdata:sync:connection:<name>` instead.' }); return }
    const reference = /^\s*password_secret\s*:\s*['"]?([^'"\s]*)/i.exec(line)
    if (reference && reference[1] !== '') {
      const why = referenceProblem(reference[1])
      if (why) { found.push({ line: i + 1, message: `has a \`password_secret\` that is not a reference. ${why}` }); return }
    }
    if (/connection_string/i.test(line)) {
      const inString = /(?:password|pwd)\s*=\s*([^;'"\s]+)/i.exec(line)
      if (inString && !inString[1].startsWith('secret:') && !inString[1].startsWith('env:')) found.push({ line: i + 1, message: 'has a password in its connection string. Keep the string without it and name the secret.' })
    }
  })
  return found
}

const NAME = /^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$/

/** Why a replication's or a connection's name would be refused as you type it, or null (it is a folder and a file name, and the connection name is what mappings quote). */
export function syncNameProblem(name: string, taken: readonly string[], path: (name: string) => string): string | null {
  if (name === '') return null
  if (!NAME.test(name)) return 'Letters, digits, "-", "_" and ".", starting with a letter or digit, at most 64.'
  return taken.includes(path(name)) ? `${path(name)} already exists in this draft.` : null
}

export const replicationPath = (name: string) => `config/replications/${name}/task.yaml`
export const connectionPath = (name: string) => `config/connections/${name}.yaml`

/** The names of the connections a draft has, from its file list. */
export const connectionsOf = (paths: readonly string[]): string[] =>
  paths.map((p) => /^config\/connections\/([^/]+)\.ya?ml$/i.exec(p)?.[1]).filter((n): n is string => !!n).sort()

/** The names of the replications a draft has. */
export const replicationsOf = (paths: readonly string[]): string[] =>
  [...new Set(paths.map((p) => /^config\/replications\/([^/]+)\/task\.ya?ml$/i.exec(p)?.[1]).filter((n): n is string => !!n))].sort()

/** A new connection file: named, with its secret by reference. Same shape as the project's template. */
export function newConnectionText(name: string, engine: string): string {
  return ['schema: connection/1', `name: ${name}`, `engine: ${engine}`, 'address:', '  host: server.example.com', '  database: db', 'auth:', '  mode: sql', '  user: sync', `  password_secret: secret:dbdata:sync:connection:${name}`, ''].join('\n')
}

/** A new replication: its task (periodic, every 15 minutes, in the current format) and one table mapping between the two connections it names. The mapping holds a column cache with one `id` column on each side: edit it to the table's columns. */
export function newReplicationFiles(name: string, source: string, target: string): { path: string; text: string }[] {
  const task = ['schemaVersion: 2', `name: ${name}`, 'scheduling:', '  mode: Periodic', "  cronExpression: '*/15 * * * *'",
    'changeProcessing:', '  reader:', '    kind: MsSqlChangeTracking', '    options: {}', '  cache:', '    kind: MsSqlStagingTable', '    options: {}', '  writer:', '    kind: PostgresMerge', '    options: {}',
    'bulkLoad:', '  reader:', '    kind: BatchReload', '    options: {}',
    'endpoints:', '  source:', `    connectionName: ${source}`, '  target:', `    connectionName: ${target}`,
    'scripts: {}', 'hooks: {}', 'provisioning: {}', 'reconcile:', '  afterChange:', '    mode: none', '  deleteGuard:', '    mode: ratio', '    maxRatio: 0.5', 'segmentingStrategies: []', ''].join('\n')
  const mapping = ['schemaVersion: 1', `name: ${name}`, 'sources:', `- connectionName: ${source}`, '  schema: dbo', `  table: ${name}`, 'targets:', `- connectionName: ${target}`, '  schema: public', `  table: ${name}`,
    'columnMappings: []', 'relationships: []', 'scripts: {}', 'hooks: {}', 'provisioning: {}', 'verification: []', 'defaultSegmenting: []', 'primaryKey: []',
    'sourceColumns:', '- name: id', '  nativeType: bigint', '  isPrimaryKey: true', '  keyOrdinal: 1', 'targetColumns:', '- name: id', '  nativeType: bigint', '  isPrimaryKey: true', '  keyOrdinal: 1', 'relationshipColumns: {}', ''].join('\n')
  return [{ path: replicationPath(name), text: task }, { path: `config/replications/${name}/table-mappings/${name}.yaml`, text: mapping }]
}
