import { useRef, useState } from 'react'
import { ErrorBanner, Field, Link, NotesPanel, SubTabs, useNavigate } from '@dbdatakit/shell'
import { filesPath, syncNameProblem, useDraft, useProvisioningPlan } from '@dbdatakit/workspace'
import { useInferredColumns } from '../api/hooks'
import { EndpointSidePair } from '../components/EndpointSidePair'
import { ScriptBindingsCard } from '../components/ScriptBindings'
import { columnsOf, findTable, tablesIn, useCacheConnection, useColumns, useTables } from '../metadata'
import { mappingPath, newMappingText } from '../model'
import { ReconcileConfigCard, reconcileOf } from '../replication/ReconcileConfigCard'
import { resolveSide } from '../resolveEndpoint'
import { tableExists } from '../tableExists'
import type { ColumnMapping, ColumnMetadata, ReconcileConfig, SourceTableSpec, TableMappingConfig, TableSpec } from '../types'
import type { SyncDraft } from '../useSyncDraft'
import { CachedMetadataCard } from './CachedMetadataCard'
import { ChecksCard } from './ChecksCard'
import { ColumnMappingEditor } from './ColumnMappingEditor'
import { DefaultSegmentingCard } from './DefaultSegmentingCard'
import { MappingPipelineCard, type PipelineOverrides } from './MappingPipelineCard'
import { MappingSide } from './MappingSide'
import { mappingTabs, tabOrDefault } from './mappingTabs'
import { PrimaryKeyCard } from './PrimaryKeyCard'
import { pendingSteps, ProvisioningCard } from './ProvisioningCard'
import { RelationshipsCard } from './RelationshipsCard'
import { SourceFilterCard } from './SourceFilterCard'
import { nextMappings, suggestColumnMappings } from './suggestMappings'
import { MappingRowsPreview } from './MappingRowsPreview'
import { NewestPreflight } from '../replication/NewestPreflight'
import { QueryColumnsCard } from './QueryColumnsCard'

const emptySource: SourceTableSpec = { connectionName: null, database: null, schema: '', table: '', filter: null, query: null }
const emptyTarget: TableSpec = { connectionName: null, database: null, schema: '', table: '' }

/** Whether two specs name the same table: all four parts, since the same `dbo.Orders` on another database is another table. */
const samePlace = (a: TableSpec, b: TableSpec) => a.connectionName === b.connectionName && a.database === b.database && a.schema === b.schema && a.table === b.table

/** The pipeline overrides a mapping states, each null where it takes the replication's stage. */
const overridesOf = (m: TableMappingConfig): PipelineOverrides => ({
  readerOverride: m.readerOverride ?? null,
  cacheOverride: m.cacheOverride ?? null,
  writerOverride: m.writerOverride ?? null,
  bulkLoadReaderOverride: m.bulkLoadReaderOverride ?? null,
  bulkLoadCacheOverride: m.bulkLoadCacheOverride ?? null,
  bulkLoadWriterOverride: m.bulkLoadWriterOverride ?? null,
})

/**
 * A table mapping's editor: what the mapping is at the top (its name, its source and target side by side, the source filter) and its tabs below,
 * each at its own address. Every change is an unsaved edit of the mapping's file in the draft, the same one Files shows; **Save mapping** saves
 * that file, **Cancel** drops its unsaved edits (a mapping made here and never saved goes with them), and **Delete** removes the file from the draft.
 *
 * Picking a table records that side's columns from the catalog cache in the mapping (`sourceColumns`, `targetColumns`), and, while nobody has
 * edited the column mappings, pairs the columns of the same name again, so a table picked and then replaced leaves none of its columns behind.
 * What is inferred (each column's target type, the natural key) is the Workspace's answer (`sync/inferred-columns`), asked with the draft's unsaved
 * edits; the Provisioning tab shows this mapping's entry of the replication's plan, made from the saved files.
 */
export function TableMappingForm({ model, name, tab, base, afterRemove }: { model: SyncDraft; name: string; tab: string; base: string; afterRemove: string }) {
  const draft = useDraft()
  const navigate = useNavigate()
  const view = model.mappingOf(name)
  const task = model.task.value
  const [error, setError] = useState<unknown>(null)
  const [queryOpen, setQueryOpen] = useState(false)
  const lastSuggested = useRef<ColumnMapping[] | null>(null)

  const mapping = view?.value ?? null
  const source = mapping?.sources[0] ?? emptySource
  const target = mapping?.targets[0] ?? emptyTarget
  const querySource = source.query !== null
  const resolvedSource = resolveSide(task?.endpoints.source ?? null, source)
  const resolvedTarget = resolveSide(task?.endpoints.target ?? null, target)
  const sourceCache = useCacheConnection(resolvedSource.connectionName || undefined).data
  const targetCache = useCacheConnection(resolvedTarget.connectionName || undefined).data
  const targetTables = useTables(resolvedTarget.connectionName || undefined, resolvedTarget.database || undefined).data
  const targetExists = tableExists(targetTables, resolvedTarget.schema, resolvedTarget.table)
  const catalogSourceColumns = useColumns(resolvedSource.connectionName || undefined, resolvedSource.database || undefined, resolvedSource.schema, querySource ? undefined : resolvedSource.table || undefined).data
  // a query's columns are not in the catalog cache: the mapping records them itself, from a description of the query
  const sourceColumns = querySource ? (mapping?.sourceColumns?.length ? mapping.sourceColumns : undefined) : catalogSourceColumns
  const catalogTargetColumns = useColumns(resolvedTarget.connectionName || undefined, resolvedTarget.database || undefined, resolvedTarget.schema, targetExists === true ? resolvedTarget.table : undefined).data
  const inferred = useInferredColumns(model.replication, mapping ? name : undefined, !!mapping && !querySource && !!source.table)
  const plan = useProvisioningPlan(draft.project, draft.id, model.replication)

  if (!view || view.loading) return <div className="empty">Loading…</div>
  if (!mapping) {
    return (
      <div className="banner warn" data-testid="mapping-problem">
        {view.problem} <Link to={filesPath(draft.project, draft.id, view.path)} className="mono">Open {view.path} in Files</Link>
      </div>
    )
  }

  const set = (next: TableMappingConfig) => model.setMapping(name, next)
  const setSource = (next: SourceTableSpec) => set({ ...mapping, sources: [next, ...mapping.sources.slice(1)] })

  /** The columns of a side's table in the cache, as the mapping records them. */
  const cachedColumns = (cache: typeof sourceCache, resolved: { database: string; schema: string; table: string }): ColumnMetadata[] | undefined => {
    const t = cache && resolved.table ? findTable(cache, resolved.database || undefined, resolved.schema, resolved.table) : undefined
    return t ? columnsOf(t) : undefined
  }

  /** A side changed: record its columns when its table moved, and follow the suggestion of the column mappings while they are untouched. */
  const setSides = (nextSource: SourceTableSpec, nextTarget: TableSpec) => {
    const next: TableMappingConfig = { ...mapping, sources: [nextSource, ...mapping.sources.slice(1)], targets: [nextTarget, ...mapping.targets.slice(1)] }
    const rs = resolveSide(task?.endpoints.source ?? null, nextSource)
    const rt = resolveSide(task?.endpoints.target ?? null, nextTarget)
    const sCols = cachedColumns(sourceCache, rs)
    const tCols = cachedColumns(targetCache, rt)
    if (!samePlace(source, nextSource)) next.sourceColumns = sCols
    if (!samePlace(target, nextTarget)) next.targetColumns = tCols
    const offered = tCols ?? (rt.table ? sCols : undefined)
    if (sCols && offered) {
      const suggested = nextMappings(mapping.columnMappings, lastSuggested.current, suggestColumnMappings(sCols, offered))
      if (suggested) { lastSuggested.current = suggested; next.columnMappings = suggested }
    }
    set(next)
  }

  /** A query's columns were described: the mapping records them, and its column mappings follow them while nobody has edited them. */
  const setQueryColumns = (cols: ColumnMetadata[]) => {
    const next: TableMappingConfig = { ...mapping, sourceColumns: cols }
    const offered = catalogTargetColumns ?? (targetExists === false && resolvedTarget.table ? cols : undefined)
    if (offered) {
      const suggested = nextMappings(mapping.columnMappings, lastSuggested.current, suggestColumnMappings(cols, offered))
      if (suggested) { lastSuggested.current = suggested; next.columnMappings = suggested }
    }
    set(next)
  }

  const setSourceTable = (spec: TableSpec) => {
    const nextSource = { ...source, ...spec } as SourceTableSpec
    // an empty target table takes the source's table; a target someone typed is left as it is
    const nextTarget = nextSource.table && !target.table ? { ...target, schema: target.schema || nextSource.schema, table: nextSource.table } : target
    setSides(nextSource, nextTarget)
  }

  // a query in place of a table drops the table's schema and name (and so its cached columns); back to a table drops the query
  const toggleQuerySource = () => setSides(querySource ? { ...source, query: null, allowSubquery: undefined } : { ...source, query: '', schema: '', table: '' }, target)

  const run = async (what: () => Promise<unknown>) => {
    setError(null)
    try { await what() } catch (e) { setError(e) }
  }
  const sourceStated = querySource ? !!source.query?.trim() : !!source.table
  // a query's columns come from describing it, so a query may be saved before any of them is mapped
  const canSave = !!resolvedSource.connectionName && sourceStated && !!resolvedTarget.connectionName && !!target.table && (querySource || mapping.columnMappings.length > 0)
  const shownTab = tabOrDefault(tab)
  const planned = plan.data?.data?.mappings.find((m) => m.mapping === name)
  const inferredTypes = inferred.data
    ? Object.fromEntries(inferred.data.columns.flatMap((c) => (c.targetType ? [[c.sourceColumn, c.targetType] as const] : [])))
    : undefined
  const writerKind = mapping.writerOverride?.kind ?? task?.changeProcessing.writer.kind
  const sourceConnection = model.connections.find((c) => c.name === resolvedSource.connectionName)
  const overrides = overridesOf(mapping)

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }} data-testid="mapping-editor" data-name={name}>
      <div className="page-head">
        <h2 className="page-title mono">{name}</h2>
        {view.isNew ? <span className="badge">NEW</span> : <span className="badge badge-accent">MAPPED</span>}
        {view.dirty && <span className="badge" data-testid="mapping-dirty">not saved</span>}
        <div className="right">
          <button type="button" className="btn" disabled={!view.dirty} data-testid="cancel-mapping-button"
            onClick={() => { const wasNew = view.isNew; draft.discard(view.path); if (wasNew) navigate(afterRemove) }}>Cancel</button>
          {draft.canEdit && (
            <button type="button" className="btn btn-danger" data-testid={`delete-mapping-${name}`}
              onClick={() => void run(async () => { await draft.remove(view.path); navigate(afterRemove) })}>Delete</button>
          )}
          {draft.canEdit && (
            <button type="button" className="btn btn-primary" disabled={!view.dirty || !canSave || draft.saving} data-testid="save-mapping-button"
              title={canSave ? undefined : 'A mapping needs a source table or query, a target table and, for a table, at least one mapped column.'}
              onClick={() => void run(() => draft.saveOne(view.path))}>Save mapping</button>
          )}
        </div>
      </div>

      <ErrorBanner error={error} />

      <EndpointSidePair
        source={<MappingSide side="source" label="Source" inherited={task?.endpoints.source ?? null} spec={source} connections={model.connections} onChange={setSourceTable} testIdPrefix="source"
          query={{
            isQuerySource: querySource,
            onToggleQuerySource: toggleQuerySource,
            value: source.query ?? '',
            onChange: (query) => setSource({ ...source, query }),
            allowSubquery: source.allowSubquery !== false,
            onAllowSubqueryChange: (next) => setSource({ ...source, allowSubquery: next ? undefined : false }),
            open: queryOpen,
            onOpenChange: setQueryOpen,
          }} />}
        target={<MappingSide side="target" label="Target" inherited={task?.endpoints.target ?? null} spec={target} connections={model.connections} onChange={(t) => setSides(source, { ...target, ...t })} testIdPrefix="target" allowNewTable />}
      />

      <SourceFilterCard filter={source.filter} onChange={(filter) => setSource({ ...source, filter })} editable={draft.canEdit} />

      {/* a mapping that was never saved is in no published version, so there is nothing of it to read yet */}
      {!view.isNew && <MappingRowsPreview replication={model.replication} mapping={name} />}

      <SubTabs base={base} tabs={mappingTabs(base, pendingSteps(planned))} testId="mapping-subtabs" />

      <div className="subtab-panel" data-testid={`mapping-panel-${shownTab}`}>
        {shownTab === 'notes' && (
          <NotesPanel value={mapping.notes} onChange={(notes) => set({ ...mapping, notes })} subject="this table mapping" testId="mapping-notes" editable={draft.canEdit} />
        )}
        {shownTab === 'columns' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            {querySource && (
              <QueryColumnsCard connection={resolvedSource.connectionName} query={source.query ?? ''} columns={mapping.sourceColumns} onColumns={setQueryColumns} editable={draft.canEdit} />
            )}
            <ColumnMappingEditor
              resolvedSource={resolvedSource}
              target={resolvedTarget}
              mappings={mapping.columnMappings}
              onChange={(columnMappings) => set({ ...mapping, columnMappings })}
              targetExists={targetExists}
              sourceColumns={sourceColumns}
              catalogTargetColumns={catalogTargetColumns}
              relationships={mapping.relationships}
              inferredTypes={inferredTypes}
              querySource={querySource}
            />
            <PrimaryKeyCard columnMappings={mapping.columnMappings} sourceColumns={sourceColumns} value={mapping.primaryKey} onChange={(primaryKey) => set({ ...mapping, primaryKey })} />
            <RelationshipsCard resolvedSource={resolvedSource} sourceColumns={sourceColumns} relationships={mapping.relationships} onChange={(relationships) => set({ ...mapping, relationships })} />
            <CachedMetadataCard mapping={mapping} source={resolvedSource} target={resolvedTarget} sourceNow={sourceColumns} targetNow={catalogTargetColumns} querySource={querySource} />
          </div>
        )}
        {shownTab === 'transforms' && (
          // the mapping is the most specific level: it inherits the replication's binding of a slot, else the source connection's
          <ScriptBindingsCard bindings={mapping.scripts ?? {}} inherited={{ ...sourceConnection?.scripts, ...task?.scripts }} level="mapping"
            onChange={(scripts) => set({ ...mapping, scripts })} editable={draft.canEdit} />
        )}
        {shownTab === 'segmenting' && (
          <DefaultSegmentingCard segments={mapping.defaultSegmenting ?? []} strategies={task?.segmentingStrategies ?? []}
            onChange={(defaultSegmenting) => set({ ...mapping, defaultSegmenting: defaultSegmenting.length > 0 ? defaultSegmenting : null })} editable={draft.canEdit} />
        )}
        {shownTab === 'provisioning' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <span className="hint" data-testid="mapping-provisioning-saved-only">
                {view.isNew
                  ? 'The plan is made from the saved files: this mapping has none yet, so it is planned once it is saved.'
                  : view.dirty
                  ? 'The plan is made from the saved files: this mapping\'s unsaved edits are not in it until saved.'
                  : 'The plan is made from the saved files and the catalog cache; it is applied from the replication\'s Provisioning tab.'}
              </span>
              <button type="button" className="btn btn-sm spacer" disabled={plan.isFetching} onClick={() => void plan.refetch()} data-testid="mapping-provisioning-replan">
                {plan.isFetching ? 'Planning…' : 'Plan again'}
              </button>
            </div>
            <ErrorBanner error={plan.error} />
            <ProvisioningCard provisioning={mapping.provisioning ?? { createTargetTableIfMissing: null, alterTargetTableColumnsIfMissingOrChanged: null }}
              inherited={task?.provisioning} onChangeProvisioning={(provisioning) => set({ ...mapping, provisioning })} plan={planned} editable={draft.canEdit} />
            {!view.isNew && <NewestPreflight project={draft.project} id={draft.id} replication={model.replication} mapping={name} />}
          </div>
        )}
        {shownTab === 'pipeline' && task && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <MappingPipelineCard replication={model.replication} mappingName={name} task={task} sourceConnection={resolvedSource.connectionName || null}
              targetConnection={resolvedTarget.connectionName || null} overrides={overrides} onChange={(next) => set({ ...mapping, ...next })} editable={draft.canEdit} />
            <MappingReconcileCard inherited={reconcileOf(task.reconcile)} writerKind={writerKind ?? ''} reconcileOverride={mapping.reconcileOverride ?? null}
              onChange={(reconcileOverride) => set({ ...mapping, reconcileOverride })} editable={draft.canEdit} />
          </div>
        )}
        {shownTab === 'diagnostics' && (
          <MappingDiagnostics traceTiming={mapping.traceTiming === true} onChange={(on) => set({ ...mapping, traceTiming: on ? true : null })} editable={draft.canEdit} />
        )}
        {shownTab === 'verify' && (
          <ChecksCard mapping={mapping} writerKind={writerKind} onChange={(verification) => set({ ...mapping, verification: verification.length > 0 ? verification : null })} editable={draft.canEdit} />
        )}
      </div>
    </div>
  )
}

/**
 * The mapping's own delete reconciliation in place of the replication's (`reconcileOverride:`): **Override here** starts from what is in effect,
 * and off takes the replication's again. It replaces the whole setting, cadence, after-change trigger and guard together.
 */
function MappingReconcileCard({ inherited, writerKind, reconcileOverride, onChange, editable }: {
  inherited: ReconcileConfig
  writerKind: string
  reconcileOverride: ReconcileConfig | null
  onChange: (next: ReconcileConfig | null) => void
  editable: boolean
}) {
  const overriding = reconcileOverride !== null
  const effective = reconcileOverride ?? inherited
  return (
    <>
      <div className="row" style={{ gap: 8, padding: '0 2px' }}>
        <span className="card-title sm">Delete reconciliation</span>
        {!overriding && <span className="badge">INHERITED</span>}
        <span className="spacer row" style={{ gap: 7 }}>
          <button type="button" className={`toggle ${overriding ? 'on' : ''}`} disabled={!editable} onClick={() => onChange(overriding ? null : structuredClone(effective))}
            aria-pressed={overriding} data-testid="mapping-reconcile-override" />
          <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Override here</span>
        </span>
      </div>
      {overriding
        ? <ReconcileConfigCard reconcile={effective} writerKind={writerKind} onChange={onChange} editable={editable} />
        : (
          <span className="hint" data-testid="mapping-reconcile-inherited">
            This mapping sweeps deletes as the replication does{inherited.enabled ? '' : ', which is not at all'}. Overriding replaces the whole setting:
            cadence, after-change trigger and guard together.
          </span>
        )}
    </>
  )
}

/**
 * What the mapping records about its own passes (`traceTiming:`): how long the reader, staging and writer each took, onto every run of this
 * mapping on a Runner. Off is not measured at all, so a mapping that never asked pays nothing for it. Mapping-level only, never inherited.
 */
function MappingDiagnostics({ traceTiming, onChange, editable }: { traceTiming: boolean; onChange: (on: boolean) => void; editable: boolean }) {
  return (
    <div className="card" data-testid="mapping-diagnostics">
      <div className="card-head">
        <span className="card-title">Diagnostics</span>
        <span className="card-note">what this mapping records about its own passes</span>
      </div>
      <div className="card-body" style={{ gap: 12 }}>
        <div className="row" style={{ gap: 9 }}>
          <button type="button" className={`toggle ${traceTiming ? 'on' : ''}`} disabled={!editable} onClick={() => onChange(!traceTiming)} aria-pressed={traceTiming} data-testid="trace-timing-toggle" />
          <span style={{ font: '500 12.5px var(--ui)', color: 'var(--ink-2)' }}>Trace stage timing</span>
        </div>
        <span className="hint">
          Records how long the reader, staging and writer each took onto every run of this mapping, where the Runner shows its runs. Turn it on for a
          table that behaves oddly and off again afterwards; runs already traced keep their numbers.
        </span>
      </div>
    </div>
  )
}

/**
 * A new mapping: its name and, from the catalog cache, its source table (the name follows the table as `schema.table` until someone types one). It
 * is made in the draft as an unsaved file whose target mirrors the source's table, and its editor opens.
 */
export function NewMappingForm({ model, pathOf, cancelPath }: { model: SyncDraft; pathOf: (mapping: string) => string; cancelPath: string }) {
  const draft = useDraft()
  const navigate = useNavigate()
  const source = model.task.value?.endpoints.source ?? null
  const cache = useCacheConnection(source?.connectionName ?? undefined)
  const tables = cache.data ? tablesIn(cache.data, source?.database ?? undefined) : []
  const [name, setName] = useState('')
  const [touched, setTouched] = useState(false)
  const [picked, setPicked] = useState(-1)
  const table = tables[picked]
  const path = (n: string) => mappingPath(model.replication, n)
  const problem = syncNameProblem(name.trim(), draft.paths, path)

  const create = (e: React.FormEvent) => {
    e.preventDefault()
    const n = name.trim()
    if (n === '' || problem) return
    const side = table ? { schema: table.schema, table: table.name } : { schema: '', table: '' }
    draft.edit(path(n), newMappingText(n, side, side))
    navigate(pathOf(n))
  }

  return (
    <form className="card" onSubmit={create} data-testid="new-mapping-form">
      <div className="card-head"><span className="card-title">New table mapping</span></div>
      <div className="card-body stack">
        <Field label="Source table">
          <select className="select" value={String(picked)} data-testid="new-mapping-table"
            onChange={(e) => {
              const i = Number(e.target.value)
              setPicked(i)
              const t = tables[i]
              if (!touched && t) setName(t.schema ? `${t.schema}.${t.name}` : t.name)
            }}>
            <option value="-1">Pick it later…</option>
            {tables.map((t, i) => <option key={i} value={String(i)}>{t.schema ? `${t.schema}.` : ''}{t.name}</option>)}
          </select>
        </Field>
        {source?.connectionName && cache.problem && <span className="hint">{cache.problem}</span>}
        <Field label="Name">
          <input className="input" required value={name} onChange={(e) => { setName(e.target.value); setTouched(true) }} data-testid="mapping-name-input" />
        </Field>
        {problem && <span className="field-error" data-testid="mapping-name-problem">{problem}</span>}
        <div className="row">
          <button type="submit" className="btn btn-primary" disabled={name.trim() === '' || !!problem || !draft.canEdit} data-testid="create-mapping-button">Create</button>
          <button type="button" className="btn" onClick={() => navigate(cancelPath)}>Cancel</button>
        </div>
      </div>
    </form>
  )
}
