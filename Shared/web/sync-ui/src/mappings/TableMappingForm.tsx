import { useRef, useState } from 'react'
import { ErrorBanner, Field, Link, SubTabs, useNavigate } from '@dbdatakit/shell'
import { filesPath, syncNameProblem, useDraft } from '@dbdatakit/workspace'
import { EndpointSidePair } from '../components/EndpointSidePair'
import { columnsOf, findTable, tablesIn, useCacheConnection, useColumns, useTables } from '../metadata'
import { mappingPath, newMappingText } from '../model'
import { resolveSide } from '../resolveEndpoint'
import { tableExists } from '../tableExists'
import type { ColumnMapping, ColumnMetadata, SourceTableSpec, TableMappingConfig, TableSpec } from '../types'
import type { SyncDraft } from '../useSyncDraft'
import { CachedMetadataCard } from './CachedMetadataCard'
import { ColumnMappingEditor } from './ColumnMappingEditor'
import { MappingSide } from './MappingSide'
import { mappingTabs, tabOrDefault } from './mappingTabs'
import { PrimaryKeyCard } from './PrimaryKeyCard'
import { RelationshipsCard } from './RelationshipsCard'
import { nextMappings, suggestColumnMappings } from './suggestMappings'

const emptySource: SourceTableSpec = { connectionName: null, database: null, schema: '', table: '', filter: null, query: null }
const emptyTarget: TableSpec = { connectionName: null, database: null, schema: '', table: '' }

/** Whether two specs name the same table: all four parts, since the same `dbo.Orders` on another database is another table. */
const samePlace = (a: TableSpec, b: TableSpec) => a.connectionName === b.connectionName && a.database === b.database && a.schema === b.schema && a.table === b.table

/**
 * A table mapping's editor: what the mapping is at the top (its name, its source and target side by side) and its tabs below. Every change is an
 * unsaved edit of the mapping's file in the draft, the same one Files shows; **Save mapping** saves that file, **Cancel** drops its unsaved edits
 * (a mapping made here and never saved goes with them), and **Delete** removes the file from the draft.
 *
 * Picking a table records that side's columns from the catalog cache in the mapping (`sourceColumns`, `targetColumns`), and, while nobody has
 * edited the column mappings, pairs the columns of the same name again, so a table picked and then replaced leaves none of its columns behind.
 */
export function TableMappingForm({ model, name, tab, base, afterRemove }: { model: SyncDraft; name: string; tab: string; base: string; afterRemove: string }) {
  const draft = useDraft()
  const navigate = useNavigate()
  const view = model.mappingOf(name)
  const task = model.task.value
  const [error, setError] = useState<unknown>(null)
  const lastSuggested = useRef<ColumnMapping[] | null>(null)

  const mapping = view?.value ?? null
  const source = mapping?.sources[0] ?? emptySource
  const target = mapping?.targets[0] ?? emptyTarget
  const resolvedSource = resolveSide(task?.endpoints.source ?? null, source)
  const resolvedTarget = resolveSide(task?.endpoints.target ?? null, target)
  const sourceCache = useCacheConnection(resolvedSource.connectionName || undefined).data
  const targetCache = useCacheConnection(resolvedTarget.connectionName || undefined).data
  const targetTables = useTables(resolvedTarget.connectionName || undefined, resolvedTarget.database || undefined).data
  const targetExists = tableExists(targetTables, resolvedTarget.schema, resolvedTarget.table)
  const sourceColumns = useColumns(resolvedSource.connectionName || undefined, resolvedSource.database || undefined, resolvedSource.schema, resolvedSource.table || undefined).data
  const catalogTargetColumns = useColumns(resolvedTarget.connectionName || undefined, resolvedTarget.database || undefined, resolvedTarget.schema, targetExists === true ? resolvedTarget.table : undefined).data

  if (!view || view.loading) return <div className="empty">Loading…</div>
  if (!mapping) {
    return (
      <div className="banner warn" data-testid="mapping-problem">
        {view.problem} <Link to={filesPath(draft.project, draft.id, view.path)} className="mono">Open {view.path} in Files</Link>
      </div>
    )
  }

  const set = (next: TableMappingConfig) => model.setMapping(name, next)

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

  const setSource = (spec: TableSpec) => {
    const nextSource = { ...source, ...spec } as SourceTableSpec
    // an empty target table takes the source's table; a target someone typed is left as it is
    const nextTarget = nextSource.table && !target.table ? { ...target, schema: target.schema || nextSource.schema, table: nextSource.table } : target
    setSides(nextSource, nextTarget)
  }

  const run = async (what: () => Promise<unknown>) => {
    setError(null)
    try { await what() } catch (e) { setError(e) }
  }
  const canSave = !!resolvedSource.connectionName && !!source.table && !!resolvedTarget.connectionName && !!target.table && mapping.columnMappings.length > 0
  const shownTab = tabOrDefault(tab)

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
              title={canSave ? undefined : 'A mapping needs a source and a target table and at least one mapped column.'}
              onClick={() => void run(() => draft.saveOne(view.path))}>Save mapping</button>
          )}
        </div>
      </div>

      <ErrorBanner error={error} />

      <EndpointSidePair
        source={<MappingSide side="source" label="Source" inherited={task?.endpoints.source ?? null} spec={source} connections={model.connections} onChange={setSource} testIdPrefix="source" />}
        target={<MappingSide side="target" label="Target" inherited={task?.endpoints.target ?? null} spec={target} connections={model.connections} onChange={(t) => setSides(source, { ...target, ...t })} testIdPrefix="target" allowNewTable />}
      />

      <SubTabs base={base} tabs={mappingTabs(base)} testId="mapping-subtabs" />

      <div className="subtab-panel">
        {shownTab === 'columns' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <ColumnMappingEditor
              resolvedSource={resolvedSource}
              target={resolvedTarget}
              mappings={mapping.columnMappings}
              onChange={(columnMappings) => set({ ...mapping, columnMappings })}
              targetExists={targetExists}
              sourceColumns={sourceColumns}
              catalogTargetColumns={catalogTargetColumns}
              relationships={mapping.relationships}
            />
            <PrimaryKeyCard columnMappings={mapping.columnMappings} sourceColumns={sourceColumns} value={mapping.primaryKey} onChange={(primaryKey) => set({ ...mapping, primaryKey })} />
            <RelationshipsCard resolvedSource={resolvedSource} sourceColumns={sourceColumns} relationships={mapping.relationships} onChange={(relationships) => set({ ...mapping, relationships })} />
            <CachedMetadataCard mapping={mapping} source={resolvedSource} target={resolvedTarget} sourceNow={sourceColumns} targetNow={catalogTargetColumns} />
          </div>
        )}
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
