import { useMemo, useState } from 'react'
import { useNavigate } from '@dbdatakit/shell'
import { useDraft } from '@dbdatakit/workspace'
import { RefreshPanel } from '../components/RefreshPanel'
import { tablesIn, useCacheConnection } from '../metadata'
import type { SyncDraft } from '../useSyncDraft'
import { createMappings, type CreateNote } from './createMappings'

const COLUMNS = '26px 1fr 90px'

/** A table's identity for the ticks and the counts: two parts, never joined with a dot, since a name may hold one. */
const keyOf = (t: { schema: string; table: string }) => JSON.stringify([t.schema, t.table])
const labelOf = (t: { schema: string; table: string }) => (t.schema ? `${t.schema}.${t.table}` : t.table)

/**
 * The tables of the replication's source database, from the draft's catalog cache, with how many of the replication's mappings read each one, and
 * **Create mappings** for the ticked ones. A mapping made here states its tables and nothing else, so it follows the replication; the files are
 * unsaved edits of the draft until Save. Select-all acts on the tables shown, never on ones a filter hides. **Refresh** reads the source connection
 * again from a Runner channel and shows what changed before the cache is replaced; accepted, a new table is listed here at once, before any save.
 */
export function MappingsOverview({ model, pathOf }: { model: SyncDraft; pathOf: (mapping: string) => string }) {
  const draft = useDraft()
  const navigate = useNavigate()
  const task = model.task.value
  const source = task?.endpoints.source ?? null
  const target = task?.endpoints.target ?? null
  const sourceCache = useCacheConnection(source?.connectionName ?? undefined)
  const targetCache = useCacheConnection(target?.connectionName ?? undefined)
  const tables = useMemo(() => (sourceCache.data ? tablesIn(sourceCache.data, source?.database ?? undefined).map((t) => ({ schema: t.schema, table: t.name })) : undefined), [sourceCache.data, source?.database])

  const [filter, setFilter] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [notes, setNotes] = useState<CreateNote[]>([])
  const [refreshing, setRefreshing] = useState(false)

  // a count rather than yes or no: two mappings may read one table, and "mapped" would hide the second
  const mappedCounts = useMemo(() => {
    const counts = new Map<string, number>()
    for (const m of model.mappings) for (const spec of m.value?.sources ?? []) counts.set(keyOf(spec), (counts.get(keyOf(spec)) ?? 0) + 1)
    return counts
  }, [model.mappings])

  const needle = filter.trim().toLowerCase()
  const shown = (tables ?? []).filter((t) => needle === '' || labelOf(t).toLowerCase().includes(needle))
  const toggle = (key: string) => setSelected((prev) => {
    const next = new Set(prev)
    if (!next.delete(key)) next.add(key)
    return next
  })
  const allShownSelected = shown.length > 0 && shown.every((t) => selected.has(keyOf(t)))
  const toggleAll = () => setSelected(allShownSelected ? new Set() : new Set(shown.map(keyOf)))

  const createSelected = () => {
    const chosen = shown.filter((t) => selected.has(keyOf(t)))
    if (chosen.length === 0) return
    const made = createMappings(model.replication, model.mappingNames, chosen,
      { endpoint: source, connection: sourceCache.data }, { endpoint: target, connection: targetCache.data })
    for (const f of made.files) draft.edit(f.path, f.text)
    setSelected(new Set())
    setNotes(made.notes)
    // straight to the first new mapping only when there is nothing to say: a note has to be read where the person is
    if (made.created.length > 0 && made.notes.length === 0) navigate(pathOf(made.created[0]))
  }

  return (
    <>
      {notes.length > 0 && (
        <div className="card flush" data-testid="bulk-create-notes">
          <div className="card-head tight">
            <span className="card-title sm">Created, but not fully captured</span>
            <span className="card-note">these mappings are in the draft and follow the replication, but a side of each is not in the catalog cache</span>
          </div>
          {notes.map((note) => (
            <div key={`${note.mapping}:${note.side}`} className="grid-row" style={{ gridTemplateColumns: '1.1fr 58px 2fr', gap: 8 }} data-testid={`bulk-create-note-${note.mapping}`}>
              <span className="name">{note.mapping}</span>
              <span className="faint">{note.side}</span>
              <span title={note.reason}>{note.reason}</span>
            </div>
          ))}
        </div>
      )}

      <div className="card flush" data-testid="mappings-overview">
        <div className="card-head tight">
          <span className="card-title sm">Source tables</span>
          <span className="card-note">
            {source?.connectionName
              ? <>{source.connectionName}{source.database ? ` · ${source.database}` : ''}</>
              : <>this replication has no source endpoint yet: pick one on the Overview tab</>}
          </span>
          {source?.connectionName && draft.canEdit && (
            <button type="button" className="btn btn-sm" disabled={refreshing} onClick={() => setRefreshing(true)} data-testid="mappings-refresh-button"
              title={`Read the tables of ${source.connectionName} again from a Runner channel`}>Refresh</button>
          )}
          <input className="input sm spacer" style={{ maxWidth: 220 }} placeholder="Filter tables…" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter tables" data-testid="table-filter" />
          <button type="button" className="btn btn-sm btn-primary" disabled={selected.size === 0 || !draft.canEdit} onClick={createSelected} data-testid="create-mappings-button">
            {selected.size === 0 ? 'Create mappings' : `Create ${selected.size} mapping${selected.size === 1 ? '' : 's'}`}
          </button>
        </div>

        {refreshing && source?.connectionName && (
          <div style={{ padding: '0 14px', borderBottom: '1px solid var(--row-edge)' }}>
            <RefreshPanel key={source.connectionName} connection={source.connectionName} onClose={() => setRefreshing(false)} testId="mappings-refresh" />
          </div>
        )}

        <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 0, height: 29 }}>
          <input type="checkbox" checked={allShownSelected} onChange={toggleAll} aria-label="Select all tables" data-testid="select-all-tables" />
          <span>Table</span><span>Mappings</span>
        </div>

        {shown.map((t) => {
          const key = keyOf(t)
          const label = labelOf(t)
          const count = mappedCounts.get(key) ?? 0
          return (
            <div key={key} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 0 }}>
              <input type="checkbox" checked={selected.has(key)} onChange={() => toggle(key)} aria-label={`Select ${label}`} data-testid={`select-table-${label}`} />
              <span className="name">{label}</span>
              <span data-testid={`table-mapping-count-${label}`}>
                {count === 0 ? <span className="faint">unmapped</span> : <span className="badge badge-accent">{count}</span>}
              </span>
            </div>
          )
        })}

        {tables && shown.length === 0 && <div className="empty">{needle ? 'No table matches that filter.' : 'No tables in this database.'}</div>}
        {!tables && source?.connectionName && <div className="empty" data-testid="mappings-overview-no-cache">{sourceCache.problem ?? 'Reading the catalog cache…'}</div>}
      </div>
    </>
  )
}
