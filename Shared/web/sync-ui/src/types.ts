/**
 * The parts of a replication set's files the Designer's screens read and write, typed as Sync's configuration reader reads them
 * (`DbDataKit.Sync.Core.Config`): a replication's `task.yaml` (format 2), a table mapping (format 1) and a connection (read only).
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
  scripts?: ScriptBindings
  /** What every mapping may do to its target unless the mapping says otherwise; absent resolves to off. */
  provisioning?: ProvisioningConfig | null
  /** The delete reconciliation of every mapping unless a mapping overrides it. */
  reconcile?: ReconcileConfig | null
  /** Named ways of dividing a table for a reload, referenced by name from the mappings. */
  segmentingStrategies?: SegmentingStrategyConfig[] | null
  /** Markdown: what the next person needs to know about the replication. */
  notes?: string | null
  /** Where the tracking state is kept; absent is the default. */
  tracking?: TrackingConfig | null
  /** The quality checks of the replication (`check/1`), each as the file writes it. */
  checks?: CheckForm[] | null
  [key: string]: unknown
}

export interface ScriptBinding {
  scriptName: string
  parameters: Record<string, string>
}

/** Keyed by slot. An absent key takes a broader level's binding; a key with a null value binds nothing here, overriding a broader one. */
export type ScriptBindings = Record<string, ScriptBinding | null>

/** What may be done to a target's shape without being asked. Null at either level is "nobody here has said": a mapping takes the replication's, a replication resolves to off. */
export interface ProvisioningConfig {
  createTargetTableIfMissing: boolean | null
  alterTargetTableColumnsIfMissingOrChanged: boolean | null
}

/** `tracking:` of a replication: `mode` target, state or both, `primary` only for both, and how much of the state table on the target is kept. */
export interface TrackingConfig {
  mode: 'target' | 'state' | 'both' | null
  primary?: 'target' | 'state' | null
  retention?: TrackingRetention | null
}

/** `tracking.retention`: an entry goes only when it is older than `days` and not among the newest `entries` of its mapping. */
export interface TrackingRetention {
  entries?: number | null
  days?: number | null
}

/** Whether a reconcile sweep also runs after changes a pass found, besides its own cadence. */
export type AfterChangeStrategy = { mode: 'none' } | { mode: 'any' }

/** How much of a segment's rows a sweep may delete before the writer refuses. */
export type DeleteGuard = { mode: 'none' } | { mode: 'ratio'; maxRatio: number }

/** `reconcile:` of a replication, or a mapping's `reconcileOverride`. */
export interface ReconcileConfig {
  enabled: boolean
  /** The sweep's own cadence; null runs it only after changes or when a person asks. */
  every: SchedulingConfig | null
  afterChange: AfterChangeStrategy
  /** A sweep after every N successful change passes; null is off. */
  afterEveryPasses?: number | null
  deleteGuard: DeleteGuard
  [key: string]: unknown
}

/** Which slice of a source table one reload covers (`defaultSegmenting` of a mapping). Range is half open: `rangeMin` inclusive, `rangeMax` exclusive. */
export type BatchReloadSegment =
  | { mode: 'full' }
  | { mode: 'list'; column: string; values: string[] }
  | { mode: 'range'; column: string; rangeMin: string; rangeMax: string; label?: string | null }
  | { mode: 'auto'; column: string; bucketCount: number }
  /** A named strategy of the replication, expanded each time it runs; `column` is this table's, not the strategy's. */
  | { mode: 'custom'; strategyName: string; column?: string | null }

export type SegmentingStrategyKind = 'DuckDb' | 'SourceSql' | 'TargetSql' | 'Script'

/** A named way of dividing a table for a reload: every kind returns the columns label, range_start, range_end and, optionally, selected. */
export interface SegmentingStrategyConfig {
  name: string
  kind: SegmentingStrategyKind
  sql?: string | null
  scriptName?: string | null
  parameters?: Record<string, string>
}

/** Whether a strategy of this kind reads a database each time it runs (every kind but DuckDB, which computes from the calendar). */
export const runsAgainstAConnection = (kind: SegmentingStrategyKind): boolean => kind !== 'DuckDb'

export type VerificationCheckKind = 'RowCount' | 'Sum' | 'Sql' | 'Script'

/** One comparison of a mapping's source and target (`verification:` of a mapping); columns are named by their target names. */
export interface VerificationCheckConfig {
  name: string
  kind: VerificationCheckKind
  groupBy: string[]
  measures: string[]
  sourceSql: string | null
  targetSql: string | null
  scriptName: string | null
  parameters: Record<string, string>
  filter: string | null
  /** Compares the target's current rows only, for a target that keeps history. */
  compareCurrentOnly?: boolean
  currentColumn?: string | null
  /** How far apart the two sides may be, as a fraction of the larger side; zero flags any difference. */
  differenceThreshold: number
}

/** One quality check of a replication as `checks:` of `task.yaml` writes it (`check/1`), its keys flat. */
export interface CheckForm {
  id?: string | null
  kind?: string | null
  dataset?: string | null
  column?: string | null
  columns?: string[] | null
  severity?: string | null
  on_error?: string | null
  description?: string | null
  max_age?: string | null
  values?: string[] | null
  min?: string | null
  max?: string | null
  references?: { dataset?: string | null; column?: string | null; columns?: string[] | null } | null
  max_increase_percent?: string | null
  max_decrease_percent?: string | null
}

/** A script of the set (`config/scripts/<name>.yaml`) as the Designer reads it, read only: the scripts are edited in Files. */
export interface ScriptView {
  name: string
  path: string
  /** The slot it fills. */
  kind: string
  language: string
  description: string | null
  parameters: ParameterDescriptor[]
  enabled: boolean
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
  /** Whether the query may be wrapped as a subquery (segmenting, relationships and column transforms need it); absent is yes. */
  allowSubquery?: boolean
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
  scripts?: ScriptBindings
  provisioning?: ProvisioningConfig | null
  /** This mapping's own delete reconciliation in place of the replication's; null takes the replication's. */
  reconcileOverride?: ReconcileConfig | null
  verification?: VerificationCheckConfig[] | null
  /** How the table divides for a reload; empty is the whole table. */
  defaultSegmenting?: BatchReloadSegment[] | null
  /** Records how long each stage of a pass took, onto the run. */
  traceTiming?: boolean | null
  notes?: string | null
  /** This mapping's own pipeline stages; null takes the replication's stage, kind and options together. */
  readerOverride?: StageConfig | null
  cacheOverride?: StageConfig | null
  writerOverride?: StageConfig | null
  bulkLoadReaderOverride?: StageConfig | null
  bulkLoadCacheOverride?: StageConfig | null
  bulkLoadWriterOverride?: StageConfig | null
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

export type ParameterType = 'Text' | 'Number' | 'Bool' | 'Date' | 'DateTime' | 'Dropdown' | 'ColumnPicker' | 'Property' | 'Secret' | 'Sql'

/**
 * A setting a reader, a staging provider, a writer or a script declares beside the code that reads it (`ParameterDescriptor` of Sync's
 * configuration): the form is drawn from it, so a new driver or script needs no change to the screens.
 */
export interface ParameterDescriptor {
  name: string
  label?: string | null
  description?: string | null
  type?: ParameterType
  required?: boolean
  /** How many values it takes; absent is one. A vararg keeps its values under `<name>.<key>` in the flat options. */
  cardinality?: { min: number; max: number } | null
  dropdownOptions?: string[] | null
  dropdownLabels?: Record<string, string> | null
  default?: string | null
  layout?: { card?: string; group?: string; size?: number } | null
  /** Whether the declarer shows it now; absent is shown. */
  visible?: boolean
}
