import { useState } from 'react'
import { ErrorBanner, Field, KeyValueTable } from '@dbdatakit/shell'
import { ParameterForm } from '../components/ParameterForm'
import { useCapabilities, useInferredColumns } from '../api/hooks'
import { readerNotes } from '../api/readerNotes'
import { RECONCILE_ONLY_KINDS, writerNote, type DriverCapabilities, type InferredNaturalKey } from '../api/types'
import { useSyncScripts } from '../useSyncDraft'
import { NATURAL_KEY, versionsRows, withoutNaturalKey } from './naturalKey'
import type { ParameterDescriptor, ReplicationTaskConfig, StageConfig } from '../types'

/** The three stages a mapping may answer for itself, and what each is called on screen. */
export type Stage = 'reader' | 'cache' | 'writer'
const STAGES: { id: Stage; label: string }[] = [{ id: 'reader', label: 'Reader' }, { id: 'cache', label: 'Staging' }, { id: 'writer', label: 'Writer' }]

/** The two pipelines: Change Processing (the ongoing changes) and Bulk Load (a reload, and the initial load). */
export type Pipeline = 'changeProcessing' | 'bulkLoad'
const PIPELINES: { id: Pipeline; label: string }[] = [{ id: 'changeProcessing', label: 'Change Processing' }, { id: 'bulkLoad', label: 'Bulk Load' }]

/** The six overrides as the mapping keeps them, three per pipeline; null takes the replication's stage. */
export interface PipelineOverrides {
  readerOverride: StageConfig | null
  cacheOverride: StageConfig | null
  writerOverride: StageConfig | null
  bulkLoadReaderOverride: StageConfig | null
  bulkLoadCacheOverride: StageConfig | null
  bulkLoadWriterOverride: StageConfig | null
}

export const FIELD_OF: Record<Pipeline, Record<Stage, keyof PipelineOverrides>> = {
  changeProcessing: { reader: 'readerOverride', cache: 'cacheOverride', writer: 'writerOverride' },
  bulkLoad: { reader: 'bulkLoadReaderOverride', cache: 'bulkLoadCacheOverride', writer: 'bulkLoadWriterOverride' },
}

const stageOf = (v: unknown): StageConfig | undefined => {
  if (typeof v !== 'object' || v === null) return undefined
  const s = v as { kind?: unknown; options?: unknown }
  return { kind: typeof s.kind === 'string' ? s.kind : '', options: (typeof s.options === 'object' && s.options !== null ? s.options : {}) as Record<string, string> }
}

/**
 * What a mapping inherits for a pipeline: Change Processing's stages as the replication has them; Bulk Load's reader as the replication's
 * `bulkLoad.reader` (`BatchReload` when it says nothing) and its staging and writer as the replication's `bulkLoad`, else Change Processing's,
 * as Sync resolves them.
 */
export function inheritedStages(task: ReplicationTaskConfig, pipeline: Pipeline): Record<Stage, StageConfig> {
  const cp = task.changeProcessing
  if (pipeline === 'changeProcessing') return { reader: cp.reader, cache: cp.cache, writer: cp.writer }
  const bulk = (typeof task.bulkLoad === 'object' && task.bulkLoad !== null ? task.bulkLoad : {}) as Record<string, unknown>
  return { reader: stageOf(bulk.reader) ?? { kind: 'BatchReload', options: {} }, cache: stageOf(bulk.cache) ?? cp.cache, writer: stageOf(bulk.writer) ?? cp.writer }
}

/** The overrides after turning one stage's override on (starting from what is in effect: an override is nearly always "the same, but") or off. */
export function toggledOverride(overrides: PipelineOverrides, field: keyof PipelineOverrides, effective: StageConfig | undefined): PipelineOverrides {
  return { ...overrides, [field]: overrides[field] !== null ? null : structuredClone(effective ?? { kind: '', options: {} }) }
}

/** The kinds a picker offers for a stage, each with its note; the reconcile-only kinds are never offered. */
export function kindsFor(stage: Stage, source: DriverCapabilities | undefined, target: DriverCapabilities | undefined): { kind: string; note?: string }[] {
  if (stage === 'reader') return (source?.readers ?? []).filter((r) => !RECONCILE_ONLY_KINDS.has(r.kind)).map((r) => ({ kind: r.kind, note: readerNotes(r).join(' · ') || undefined }))
  if (stage === 'writer') return (target?.writers ?? []).filter((w) => !RECONCILE_ONLY_KINDS.has(w.kind)).map((w) => ({ kind: w.kind, note: writerNote(w) }))
  return (target?.stagingProviders ?? []).map((p) => ({ kind: p.kind }))
}

/** The settings a stage's kind declares; a reader is the source driver's, staging and a writer the target driver's. */
export function parametersFor(stage: Stage, kind: string, source: DriverCapabilities | undefined, target: DriverCapabilities | undefined): ParameterDescriptor[] {
  return (stage === 'reader' ? source?.readers.find((r) => r.kind === kind)?.parameters
    : stage === 'writer' ? target?.writers.find((w) => w.kind === kind)?.parameters
    : target?.stagingProviders.find((p) => p.kind === kind)?.parameters) ?? []
}

/**
 * A mapping's pipeline: the replication's, unless this table needs something else. The same Reader, Staging, Writer stages as the replication's
 * Pipeline tab, each with **Override here**. An override replaces its stage whole, kind and options together, never half of each. The kinds come
 * from each side's driver (`sync/capabilities`): the reader from the source connection's, staging and the writer from the target's.
 */
export function MappingPipelineCard({ replication, mappingName, task, sourceConnection, targetConnection, overrides, onChange, editable = true }: {
  replication: string
  /** The mapping's saved name, for the derived natural key; undefined for a mapping not saved yet. */
  mappingName: string | undefined
  task: ReplicationTaskConfig
  sourceConnection: string | null
  targetConnection: string | null
  overrides: PipelineOverrides
  onChange: (next: PipelineOverrides) => void
  editable?: boolean
}) {
  const [pipeline, setPipeline] = useState<Pipeline>('changeProcessing')
  const [stage, setStage] = useState<Stage>('writer')
  const { scripts } = useSyncScripts()
  const sourceCapabilities = useCapabilities(sourceConnection)
  const targetCapabilities = useCapabilities(targetConnection)
  const source = sourceCapabilities.data
  const target = targetCapabilities.data

  const inherited = inheritedStages(task, pipeline)
  const field = FIELD_OF[pipeline][stage]
  const override = overrides[field]
  const effective = override ?? inherited[stage]
  const overriding = override !== null
  const setStageValue = (patch: Partial<StageConfig>) => onChange({ ...overrides, [field]: { ...effective, ...patch } })
  const kindOf = (id: Stage) => (overrides[FIELD_OF[pipeline][id]] ?? inherited[id]).kind || '…'

  const writerField = FIELD_OF[pipeline].writer
  const writerOverride = overrides[writerField]
  // present, not filled: an override switched on and not typed into yet is still one being made
  const stated = writerOverride?.options !== undefined && NATURAL_KEY in writerOverride.options
  /** Stating a natural key overrides the writer (the key lives in its options); null takes the key out, and the writer derives it again. */
  const setNaturalKey = (next: string | null) => {
    const base = writerOverride ?? structuredClone(inherited.writer)
    const options = { ...base.options }
    if (next === null) delete options[NATURAL_KEY]
    else options[NATURAL_KEY] = next
    onChange({ ...overrides, [writerField]: { ...base, options } })
  }

  const kinds = kindsFor(stage, source, target)
  const kind = effective.kind
  const known = kinds.some((o) => o.kind === kind)
  const declared = parametersFor(stage, kind, source, target)
  const stageLabel = STAGES.find((s) => s.id === stage)!.label
  const editableParameters = stage === 'writer' && versionsRows(kind) ? withoutNaturalKey(declared) : declared
  const scriptNames = scripts.map((s) => s.name)

  return (
    <>
      <ErrorBanner error={sourceCapabilities.error ?? targetCapabilities.error} />
      <div className="card" data-testid="mapping-pipeline">
        <div className="card-head" style={{ alignItems: 'flex-start', paddingTop: 11, flexDirection: 'column', gap: 10 }}>
          <div className="row" style={{ gap: 6, alignSelf: 'center' }}>
            {PIPELINES.map((p) => (
              <button key={p.id} type="button" className={`stage ${pipeline === p.id ? 'active' : ''}`} onClick={() => { setPipeline(p.id); setStage('writer') }} data-testid={`mapping-pipeline-${p.id}`}>
                <span className="stage-name">{p.label}</span>
              </button>
            ))}
          </div>
          <div className="row" style={{ width: '100%', alignItems: 'flex-start' }}>
            <span className="card-title" style={{ width: 64, flex: 'none', paddingTop: 10 }}>Pipeline</span>
            <div className="stages">
              {STAGES.map((s, i) => (
                <span key={s.id} style={{ display: 'contents' }}>
                  {i > 0 && <span className="arrow">→</span>}
                  <button type="button" className={`stage ${stage === s.id ? 'active' : ''}`} onClick={() => setStage(s.id)} data-testid={`mapping-stage-${s.id}`}>
                    <span className="stage-name">{s.label}</span>
                    <span className="stage-impl">{kindOf(s.id)}</span>
                  </button>
                </span>
              ))}
            </div>
            <span style={{ width: 64, flex: 'none' }} />
          </div>
        </div>

        <div className="card-body" style={{ padding: 14, gap: 12 }}>
          <div className="row" style={{ gap: 8 }}>
            <span className="card-title sm">{stageLabel}</span>
            {!overriding && <span className="badge">INHERITED</span>}
            <span className="spacer row" style={{ gap: 7 }}>
              <button type="button" className={`toggle ${overriding ? 'on' : ''}`} disabled={!editable} onClick={() => onChange(toggledOverride(overrides, field, effective))} aria-pressed={overriding} data-testid={`mapping-${stage}-override`} />
              <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Override here</span>
            </span>
          </div>

          {!overriding && (
            <span className="hint" data-testid={`mapping-${stage}-inherited`}>
              {`This mapping runs the replication's ${kind || 'unset'} ${stageLabel.toLowerCase()}, with the settings the replication gives it. Overriding replaces both, this stage's kind and its settings together.`}
            </span>
          )}

          {overriding && (
            <fieldset style={{ display: 'flex', flexDirection: 'column', gap: 12, border: 0, margin: 0, padding: 0 }} disabled={!editable}>
              <Field label={`${stageLabel} implementation`}>
                <select className="select" value={kind} onChange={(e) => setStageValue({ kind: e.target.value })} data-testid={`mapping-${stage}-kind-select`}>
                  {!known && kind && <option value={kind}>{kind}, not offered by this driver</option>}
                  {kinds.map((o) => <option key={o.kind} value={o.kind}>{o.note ? `${o.kind}: ${o.note}` : o.kind}</option>)}
                </select>
              </Field>
              {editableParameters.length > 0 && (
                <ParameterForm parameters={editableParameters} values={effective.options ?? {}} onChange={(next) => setStageValue({ options: next })} options={{ script: scriptNames }} testIdPrefix={`mapping-${stage}-options`} />
              )}
              {declared.length === 0 && (
                <KeyValueTable value={effective.options ?? {}} onChange={(next) => setStageValue({ options: next })} addLabel="Setting name" testId={`mapping-${stage}-options`} />
              )}
            </fieldset>
          )}

          {/* shown whether or not the writer is overridden: the natural key is a question about this table either way */}
          {stage === 'writer' && versionsRows(kindOf('writer')) && (
            <NaturalKeyField replication={replication} mappingName={mappingName} value={writerOverride?.options?.[NATURAL_KEY] ?? ''} stated={stated} onChange={setNaturalKey} editable={editable} />
          )}
        </div>
      </div>
    </>
  )
}

/** The natural key the writer derives from the source's key, or one stated by hand. The derivation is the Workspace's (`sync/inferred-columns`), the code a run uses. */
function NaturalKeyField({ replication, mappingName, value, stated, onChange, editable }: {
  replication: string
  mappingName: string | undefined
  value: string
  stated: boolean
  /** Null takes the stated key out and goes back to deriving. */
  onChange: (next: string | null) => void
  editable: boolean
}) {
  const inferred = useInferredColumns(replication, mappingName, !stated)
  return <NaturalKeyView derived={inferred.data?.naturalKey ?? undefined} problem={inferred.data?.problem ?? (inferred.error ? 'The derived key could not be asked for.' : null)}
    mappingName={mappingName} value={value} stated={stated} onChange={onChange} editable={editable} />
}

/** The natural key field without its question. */
export function NaturalKeyView({ derived, problem, mappingName, value, stated, onChange, editable = true }: {
  derived: InferredNaturalKey | undefined
  problem: string | null
  mappingName: string | undefined
  value: string
  stated: boolean
  onChange: (next: string | null) => void
  editable?: boolean
}) {
  const why = problem ?? derived?.problem ?? null
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }} data-testid="mapping-natural-key">
      <div className="row" style={{ gap: 8 }}>
        <span className="card-title sm">Natural key</span>
        {!stated && <span className="badge">AUTO-DERIVED</span>}
        <span className="spacer row" style={{ gap: 7 }}>
          {/* stating it by hand starts from the derived columns, so the field opens with what was going to happen */}
          <button type="button" className={`toggle ${stated ? 'on' : ''}`} disabled={!editable} onClick={() => onChange(stated ? null : (derived?.columns.join(', ') ?? ''))} aria-pressed={stated} data-testid="mapping-natural-key-override" />
          <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Enter it by hand</span>
        </span>
      </div>
      {stated ? (
        <input className="input" value={value} disabled={!editable} onChange={(e) => onChange(e.target.value)} placeholder="Column, Column" data-testid="mapping-natural-key-input" />
      ) : !mappingName ? (
        <span className="hint" data-testid="mapping-natural-key-unsaved">The mapping has no file yet; once it has, this shows the key derived from its source's primary key.</span>
      ) : why ? (
        <span className="banner warn" role="alert" data-testid="mapping-natural-key-problem">{why}</span>
      ) : (
        <span className="input" style={{ display: 'flex', alignItems: 'center', background: 'var(--sunken)', borderColor: 'var(--chrome-edge)', color: 'var(--ink-4)', fontFamily: 'var(--mono)', fontSize: 12 }} data-testid="mapping-natural-key-derived">
          {derived ? derived.columns.join(', ') : '…'}
        </span>
      )}
      <span className="hint">
        The columns that identify a row across its versions: the business key, not the surrogate key this writer makes. Derived from the source table's
        primary key through this mapping's columns; enter it by hand when identity is not what the primary key says.
      </span>
    </div>
  )
}
