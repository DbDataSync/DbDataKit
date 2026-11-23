import { useState } from 'react'
import { ErrorBanner, Field, KeyValueTable } from '@dbdatakit/shell'
import { useDraft, useSyncEngines, type SyncEngineList } from '@dbdatakit/workspace'
import { ParameterForm } from '../components/ParameterForm'
import { useCapabilities } from '../api/hooks'
import { writerForReader, type DriverCapabilities } from '../api/types'
import { kindsFor, parametersFor, type Stage } from '../mappings/MappingPipelineCard'
import { versionsRows, withoutNaturalKey } from '../mappings/naturalKey'
import { useSyncScripts } from '../useSyncDraft'
import type { ConnectionView, ReplicationTaskConfig, StageConfig } from '../types'

const STAGES: { id: Stage; label: string }[] = [{ id: 'reader', label: 'Reader' }, { id: 'cache', label: 'Staging' }, { id: 'writer', label: 'Writer' }]

/** The task with one Change Processing stage changed; a reader that reads changes behind a reconciling writer brings the change writer with it. */
export function withStage(task: ReplicationTaskConfig, stage: Stage, next: StageConfig, source: DriverCapabilities | undefined, target: DriverCapabilities | undefined): ReplicationTaskConfig {
  const cp = task.changeProcessing
  if (stage !== 'reader') return { ...task, changeProcessing: { ...cp, [stage]: next } }
  const writer = next.kind !== cp.reader.kind ? writerForReader(next.kind, source?.readers ?? [], target?.writers ?? [], cp.writer.kind) : null
  return { ...task, changeProcessing: { ...cp, reader: next, ...(writer ? { writer: { kind: writer, options: {} } } : {}) } }
}

/** A connection of the draft in words for the card's head: its name and its engine's name from the registry. */
export function endpointWords(connection: string | null | undefined, connections: ConnectionView[], engines: SyncEngineList): string {
  if (!connection) return 'no connection chosen'
  const engine = connections.find((c) => c.name === connection)?.engine
  const known = engine ? engines.find(engine) : undefined
  return known ? `${connection} (${known.displayName})` : engine ? `${connection} (${engine})` : connection
}

/**
 * The replication's Change Processing pipeline as three stages, Reader, Staging and Writer, with the chosen stage's kind and its settings below.
 * The kinds come from each side's driver (`sync/capabilities`): the reader from the source connection's, staging and the writer from the target's;
 * the engines are named from the suite's registry. Every table mapping runs this pipeline unless its own Pipeline tab overrides a stage.
 */
export function PipelineCard({ draft, setDraft, connections, mappingCount, editable = true }: {
  draft: ReplicationTaskConfig
  setDraft: (next: ReplicationTaskConfig) => void
  connections: ConnectionView[]
  mappingCount: number
  editable?: boolean
}) {
  const files = useDraft()
  const engines = useSyncEngines(files.paths)
  const sourceConnection = draft.endpoints.source?.connectionName ?? null
  const targetConnection = draft.endpoints.target?.connectionName ?? null
  const sourceCapabilities = useCapabilities(sourceConnection)
  const targetCapabilities = useCapabilities(targetConnection)
  const { scripts } = useSyncScripts()
  return (
    <PipelineView draft={draft} setDraft={setDraft} mappingCount={mappingCount} editable={editable}
      source={sourceCapabilities.data} target={targetCapabilities.data} error={sourceCapabilities.error ?? targetCapabilities.error}
      sourceWords={endpointWords(sourceConnection, connections, engines)} targetWords={endpointWords(targetConnection, connections, engines)}
      scriptNames={scripts.map((s) => s.name)} />
  )
}

/** The card without its questions: the two drivers' capabilities and the draft's script names are given. */
export function PipelineView({ draft, setDraft, mappingCount, source, target, error, sourceWords, targetWords, scriptNames, editable = true }: {
  draft: ReplicationTaskConfig
  setDraft: (next: ReplicationTaskConfig) => void
  mappingCount: number
  source: DriverCapabilities | undefined
  target: DriverCapabilities | undefined
  error?: unknown
  sourceWords: string
  targetWords: string
  scriptNames: string[]
  editable?: boolean
}) {
  const [stage, setStage] = useState<Stage>('reader')
  const cp = draft.changeProcessing
  const current = cp[stage]
  const set = (patch: Partial<StageConfig>) => setDraft(withStage(draft, stage, { ...current, ...patch }, source, target))

  const kinds = kindsFor(stage, source, target)
  const known = kinds.some((o) => o.kind === current.kind)
  const declared = parametersFor(stage, current.kind, source, target)
  const historizing = stage === 'writer' && versionsRows(current.kind)
  const shown = historizing ? withoutNaturalKey(declared) : declared
  const stageLabel = STAGES.find((s) => s.id === stage)!.label
  const reader = source?.readers.find((r) => r.kind === cp.reader.kind)
  // SCD Type 2 closes a version when its key is deleted, which needs a reader that reports deletes
  const historizingWithoutDeletes = versionsRows(cp.writer.kind) && reader?.detectsDeletes === false
  const keyChangeBlind = reader?.detectsKeyChanges === false

  return (
    <>
      <ErrorBanner error={error} />
      <div className="card" data-testid="replication-pipeline">
        <div className="card-head" style={{ alignItems: 'flex-start', paddingTop: 11 }}>
          <span className="card-title" style={{ width: 64, flex: 'none', paddingTop: 10 }}>Pipeline</span>
          <div className="stages">
            {STAGES.map((s, i) => (
              <span key={s.id} style={{ display: 'contents' }}>
                {i > 0 && <span className="arrow">→</span>}
                <button type="button" className={`stage ${stage === s.id ? 'active' : ''}`} onClick={() => setStage(s.id)} aria-pressed={stage === s.id} data-testid={`stage-${s.id}`}>
                  <span className="stage-name">{s.label}</span>
                  <span className="stage-impl">{cp[s.id].kind || '…'}</span>
                </button>
              </span>
            ))}
          </div>
          <span style={{ width: 64, flex: 'none' }} />
        </div>

        <div className="card-body" style={{ padding: 14, gap: 12 }}>
          <span className="hint" data-testid="pipeline-drivers">
            {stage === 'reader' ? `The readers of the source, ${sourceWords}.` : `The ${stage === 'writer' ? 'writers' : 'staging providers'} of the target, ${targetWords}.`}
          </span>
          <fieldset style={{ display: 'grid', gridTemplateColumns: '1.5fr 1fr', gap: 16, border: 0, margin: 0, padding: 0 }} disabled={!editable}>
            <Field label={`${stageLabel} implementation`}>
              <select className="select" value={current.kind} onChange={(e) => set({ kind: e.target.value })} data-testid={`${stage}-kind-select`}>
                {!known && <option value={current.kind}>{current.kind ? `${current.kind}, not offered by this driver` : 'none chosen'}</option>}
                {kinds.map((o) => <option key={o.kind} value={o.kind}>{o.note ? `${o.kind}: ${o.note}` : o.kind}</option>)}
              </select>
            </Field>
            <Field label="Applies to">
              <span className="input" style={{ display: 'flex', alignItems: 'center', background: 'var(--sunken)', borderColor: 'var(--chrome-edge)', color: 'var(--ink-4)', fontFamily: 'var(--ui)', fontSize: 12 }} data-testid="pipeline-applies-to">
                {mappingCount === 0 ? 'No table mapping yet' : mappingCount === 1 ? 'The 1 table mapping' : `All ${mappingCount} table mappings`}
              </span>
            </Field>
          </fieldset>

          {stage === 'reader' && keyChangeBlind && (
            <span className="banner warn" role="alert" data-testid="key-change-blind-note">
              The <strong>{cp.reader.kind}</strong> reader reads only the rows that exist, so it cannot see a primary key change: after{' '}
              <span className="mono">UPDATE t SET id = 2 WHERE id = 1</span> the row with the old key stays in a target keyed by the source's key until a
              reload or a delete reconciliation sweep removes it. Turn on delete reconciliation below and set <em>Sweep after every N passes</em> to bound
              how long. That is correct for a table whose keys never change.
            </span>
          )}
          {stage === 'writer' && historizingWithoutDeletes && (
            <span className="banner warn" role="alert" data-testid="scd2-delete-blind-warning">
              The <strong>{cp.reader.kind}</strong> reader cannot report deletes, so a row deleted at the source stays marked current in this target. That
              is correct for a source that never deletes rows, and wrong for one that does.
            </span>
          )}
          {historizing && (
            <span className="hint" data-testid="natural-key-auto-derived">
              <strong>Natural key:</strong> derived from each mapping's primary key. Which columns identify a row across its versions differs per table, so
              it is answered on each table mapping's own Pipeline tab, where it can also be entered by hand.
            </span>
          )}

          <fieldset style={{ display: 'flex', flexDirection: 'column', gap: 12, border: 0, margin: 0, padding: 0 }} disabled={!editable}>
            {shown.length > 0 && (
              <ParameterForm parameters={shown} values={current.options ?? {}} onChange={(next) => set({ options: next })} options={{ script: scriptNames }} testIdPrefix={`${stage}-options`} />
            )}
            {declared.length === 0 && (
              <KeyValueTable value={current.options ?? {}} onChange={(next) => set({ options: next })} addLabel="Setting name" testId={`${stage}-options`} />
            )}
          </fieldset>
          <span className="hint">Save settings, in the page's bar, saves the pipeline with the rest of the replication.</span>
        </div>
      </div>
    </>
  )
}
