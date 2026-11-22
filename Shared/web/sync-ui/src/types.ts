/**
 * The parts of a replication set's files the Designer's screens read and write, typed as Sync's configuration reader reads them
 * (`DbDataSync.Core.Config`): a replication's `task.yaml` (format 2), a table mapping (format 1) and a connection (read only).
 * A view holds every key of its file, the ones not typed here included, so writing a view back changes only what a screen changed.
 */

export type ScheduleMode = 'Continuous' | 'Periodic'

export interface SchedulingConfig {
  mode: ScheduleMode
  frequencySeconds: number | null
  /** How long a continuous worker finds nothing before it exits; null is Sync's default (60 seconds). */
  idleTimeoutSeconds?: number | null
  cronExpression: string | null
}

export interface StageConfig {
  kind: string
  options: Record<string, string>
}

export interface ChangeProcessingConfig {
  reader: StageConfig
  cache: StageConfig
  writer: StageConfig
  /** How many incremental passes run at once (Sync's default is 4). */
  degreeOfParallelism: number
  /** How many bulk load segments and verifications run at once, on a lane of their own (Sync's default is 4). */
  bulkLoadDegreeOfParallelism: number
}

/** Where a replication reads from or writes to; every table mapping takes it unless it sets its own. */
export interface EndpointRef {
  connectionName: string | null
  database: string | null
}

export interface TaskEndpoints {
  source: EndpointRef | null
  target: EndpointRef | null
}

export interface ReplicationTaskConfig {
  schemaVersion?: number
  name: string
  enabled: boolean
  scheduling: SchedulingConfig
  changeProcessing: ChangeProcessingConfig
  endpoints: TaskEndpoints
  scripts?: Record<string, ScriptBinding | null>
  [key: string]: unknown
}

export interface ScriptBinding {
  scriptName: string
  parameters: Record<string, string>
}

/** One side of a table mapping as written: a null connection or database takes the replication's endpoint, each on its own. */
export interface TableSpec {
  connectionName: string | null
  database: string | null
  schema: string
  table: string
  [key: string]: unknown
}

export interface SourceTableSpec extends TableSpec {
  filter: string | null
  /** A query in place of a table; null for a table. */
  query: string | null
}

/** A mapping side with the replication's endpoint applied: what the pickers and the column lists read. */
export interface ResolvedRef {
  connectionName: string
  database: string
  schema: string
  table: string
}

export interface RenameStep {
  from: string
  to: string
  applied: boolean
}

export interface ColumnMapping {
  sourceColumn: string
  targetColumn: string
  transform?: string | null
  /** The target type a person chose; null (the usual case) takes the type inferred from the source's when the target is provisioned. */
  targetType?: string | null
  /** Every rename of this target column, oldest first. */
  renames?: RenameStep[]
  /** The relationship a column comes through; null for a column of the mapping's own source table. */
  relationship?: string | null
  [key: string]: unknown
}

export interface RelationshipJoinKey {
  localColumn: string
  foreignColumn: string
}

/** A named join from the mapping's source table to another table on the same connection and database. */
export interface RelationshipConfig {
  name: string
  schema: string
  table: string
  joinKeys: RelationshipJoinKey[]
}

export interface TableMappingConfig {
  schemaVersion?: number
  name: string
  sources: SourceTableSpec[]
  targets: TableSpec[]
  columnMappings: ColumnMapping[]
  relationships: RelationshipConfig[]
  /** The source columns stated as the table's key in place of what the source reports; empty uses the source's own. */
  primaryKey: string[]
  sourceColumns?: ColumnMetadata[]
  targetColumns?: ColumnMetadata[]
  columnsCapturedUtc?: string | null
  [key: string]: unknown
}

export interface TableMetadata {
  schema: string
  table: string
}

export interface ColumnMetadata {
  name: string
  nativeType: string
  isNullable: boolean
  isPrimaryKey: boolean
  keyOrdinal?: number
  isIdentity: boolean
}

/** A connection of the set as the Designer shows it (its file is edited in Files). */
export interface ConnectionView {
  name: string
  path: string
  engine: string
  /** The database the connection's address names, when it names one. */
  database: string | null
  scripts: Record<string, ScriptBinding | null>
}

/** A writer setting described by its component (the subset the screens read). */
export interface ParameterDescriptor {
  name: string
}
