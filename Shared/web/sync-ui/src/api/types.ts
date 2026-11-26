import type { ParameterDescriptor } from '../types'

// What the Workspace's sync routes answer (`DbDataWorkspace/README.md`, "A project's two areas: Files and Designer"), as Sync's libraries write it,
// and the rules the pipeline pickers apply to it.

/** Which key a reader or writer needs: the source's, the target's, a natural key, or none. */
export type KeyNeed = 'SourceKey' | 'TargetKey' | 'NaturalKey' | 'None'

/** What a pass may be asked to read next. A reader lists the incremental ones it can do; every reader can do an initial load, so it is never listed. */
export type ReadIntent = 'InitialLoad' | 'Changes' | 'ChangesFromEarliest' | 'ChangesFromLatest'

/** A reader a source connection's driver offers. */
export interface ReaderCapability {
  kind: string
  parameters: ParameterDescriptor[]
  supportsSegmentation: boolean
  /** Whether a row deleted at the source reaches the target as a delete: false for a watermark scan and a batch reload. */
  detectsDeletes: boolean
  /** Whether a key update at the source reaches the target as a delete of the old key and the row of the new one; absent is yes. */
  detectsKeyChanges?: boolean
  supportedIntents: ReadIntent[]
  needsKey?: KeyNeed
}

/** A staging provider a target connection's driver offers. */
export interface StagingCapability {
  kind: string
  parameters: ParameterDescriptor[]
}

/** A writer a target connection's driver offers. */
export interface WriterCapability {
  kind: string
  parameters: ParameterDescriptor[]
  /** Removes target rows absent from the change set within its scope; false for a writer that only adds and updates. */
  supportsReconciliation: boolean
  needsKey?: KeyNeed
}

/** `POST .../sync/capabilities`: what a connection's driver offers. Readers are the source's question, staging and writers the target's. */
export interface DriverCapabilities {
  driverType: string
  readers: ReaderCapability[]
  stagingProviders: StagingCapability[]
  writers: WriterCapability[]
  supportsConnectionTest: boolean
  supportedProvisioningActions: string[]
  /** Whether a target of this driver keeps the replication's state table, which tracking modes `target` and `both` need. */
  keepsStateTable: boolean
}

export type BindingLevel = 'connection' | 'replication' | 'mapping'

/** `GET .../sync/script-slots`: a slot a script fills and the levels it is bound at (`metadataProvider` only at a connection). */
export interface ScriptSlotInfo {
  slot: string
  levels: BindingLevel[]
  label: string
  description: string
}

/** The type a source column gets on the target, or why there is none; `fidelity` says what the translation approximates. */
export interface InferredColumnType {
  sourceColumn: string
  sourceType: string
  targetType: string | null
  fidelity: string | null
  problem: string | null
}

/** The natural key the SCD Type 2 writer derives from the source's key, in target column names, or why there is none. */
export interface InferredNaturalKey {
  columns: string[]
  problem: string | null
}

/** `POST .../sync/inferred-columns`: what would be inferred for one mapping from the source's columns in the catalog cache. */
export interface InferredColumns {
  replication: string
  mapping: string
  /** Why nothing could be inferred (the source table is not in the cache). */
  problem: string | null
  columns: InferredColumnType[]
  naturalKey: InferredNaturalKey | null
}

/** One remark of Create mappings about a side of a mapping it made. */
export interface BulkCreateNote { mapping: string; side: string; reason: string }

/** `POST .../sync/bulk-create`: the files Create mappings would make (none is written), the tables skipped and what would be inferred per mapping. */
export interface BulkCreateAnswer {
  files: { path: string; text: string }[]
  created: string[]
  skipped: string[]
  notes: BulkCreateNote[]
  inferred: Record<string, { columns: InferredColumnType[]; naturalKey: InferredNaturalKey | null }>
}

/** The change writer of every engine: the last change per key, then a delete, an update and an insert. The default behind an incremental reader. */
export const CHANGE_WRITER_KIND = 'ChangeApply'

/** What the writer pickers say next to a writer's kind. */
export function writerNote(w: WriterCapability): string {
  if (w.supportsReconciliation) return 'reconciling'
  return w.kind === CHANGE_WRITER_KIND ? 'change writer, default' : 'upsert-only'
}

/**
 * The writer to offer when the reader changes to `readerKind`: the change writer, when the reader reads changes, the target has it and the writer
 * in place reconciles (which would be refused behind that reader). Null leaves the writer alone, as it does a writer the person chose that does not
 * reconcile.
 */
export function writerForReader(readerKind: string, readers: ReaderCapability[], writers: WriterCapability[], currentWriterKind: string): string | null {
  const reader = readers.find((r) => r.kind === readerKind)
  if (!reader?.supportedIntents.includes('Changes')) return null
  if (!writers.some((w) => w.kind === CHANGE_WRITER_KIND)) return null
  const current = writers.find((w) => w.kind === currentWriterKind)
  return current?.supportsReconciliation ? CHANGE_WRITER_KIND : null
}

/** The kinds that exist only for a delete reconciliation sweep, never offered in a pipeline's pickers. */
export const RECONCILE_ONLY_KINDS: ReadonlySet<string> = new Set(['KeyReconcile', 'KeyReconcileDelete', 'KeyReconcileScd2Close'])
