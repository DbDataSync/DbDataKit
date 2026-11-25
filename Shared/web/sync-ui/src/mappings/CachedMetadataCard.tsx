import { useState } from 'react'
import { useDraft } from '@dbdatakit/workspace'
import { RefreshPanel } from '../components/RefreshPanel'
import { useReadLine } from '../metadata'
import type { ColumnMetadata, ResolvedRef, TableMappingConfig } from '../types'

/**
 * The mapping's own picture of its two tables (`sourceColumns` and `targetColumns` of the mapping file), next to what the draft's catalog cache now
 * says of the same tables: the columns are named where the two differ, so a person sees which columns moved rather than a count to go and check.
 * The mapping's picture changes only when its columns are written (a table picked, Create mappings), never by a look. **Refresh** on a side reads
 * that one table again from a Runner channel and shows what changed before the cache is replaced; accepted, the comparison here is against the new
 * cache, and the mapping's own columns stay as they were. A query source has no table in the cache: its columns are those its description recorded.
 */
export function CachedMetadataCard({ mapping, source, target, sourceNow, targetNow, querySource = false }: {
  mapping: TableMappingConfig
  source: ResolvedRef
  target: ResolvedRef
  /** The catalog cache's columns of the source table, undefined when it does not have the table. */
  sourceNow: ColumnMetadata[] | undefined
  targetNow: ColumnMetadata[] | undefined
  /** The source is a query: its recorded columns are its description's, with nothing in the cache to compare them with. */
  querySource?: boolean
}) {
  const sourceRead = useReadLine(source.connectionName || undefined)
  const targetRead = useReadLine(target.connectionName || undefined)
  return (
    <div className="card" data-testid="cached-metadata-card">
      <div className="card-head tight">
        <span className="card-title sm">Cached metadata</span>
        <span className="card-note">the columns this mapping records of its tables, against the draft's catalog cache</span>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <SideRow label="Source" cached={mapping.sourceColumns} now={sourceNow} read={sourceRead} side={source}
          note={querySource ? 'described from the source query (Query columns, above)' : undefined} />
        <SideRow label="Target" cached={mapping.targetColumns} now={targetNow} read={targetRead} side={target} />
      </div>
    </div>
  )
}

/** What changed between the mapping's columns and the catalog's, by name: added, removed, changed type. */
export function columnDifference(cached: ColumnMetadata[], now: ColumnMetadata[]): string {
  const before = new Map(cached.map((c) => [c.name, c.nativeType]))
  const after = new Map(now.map((c) => [c.name, c.nativeType]))
  const added = [...after.keys()].filter((n) => !before.has(n))
  const removed = [...before.keys()].filter((n) => !after.has(n))
  const changed = [...after.entries()].filter(([n, t]) => before.has(n) && before.get(n) !== t).map(([n]) => n)
  const parts = [
    added.length > 0 ? `${added.length} column${added.length === 1 ? '' : 's'} added (${added.join(', ')})` : null,
    removed.length > 0 ? `${removed.length} removed (${removed.join(', ')})` : null,
    changed.length > 0 ? `${changed.length} changed type (${changed.join(', ')})` : null,
  ].filter((p): p is string => p !== null)
  return parts.length === 0 ? 'the same as the catalog cache' : `the catalog cache has ${parts.join(', ')}`
}

function SideRow({ label, cached, now, read, side, note }: { label: string; cached: ColumnMetadata[] | undefined; now: ColumnMetadata[] | undefined; read: string | null; side: ResolvedRef; note?: string }) {
  const draft = useDraft()
  const [refreshing, setRefreshing] = useState(false)
  const count = cached?.length ?? 0
  const id = label.toLowerCase()
  const table = side.table ? { schema: side.schema, name: side.table } : undefined
  return (
    <div>
      <div className="row" style={{ gap: 10, alignItems: 'baseline', flexWrap: 'wrap' }} data-testid={`cached-metadata-${id}`}>
        <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)', minWidth: 54 }}>{label}</span>
        <span className="mono">{count} {count === 1 ? 'column' : 'columns'}</span>
        <span className="faint" data-testid={`cached-metadata-${id}-outcome`}>
          {note ?? (now === undefined ? 'the catalog cache does not have this table' : count === 0 ? 'none recorded yet' : columnDifference(cached ?? [], now))}
        </span>
        {read && <span className="hint">catalog {read}</span>}
        {side.connectionName && table && draft.canEdit && (
          <button type="button" className="btn btn-sm" disabled={refreshing} onClick={() => setRefreshing(true)} data-testid={`cached-metadata-${id}-refresh-button`}
            title={`Read ${side.schema ? `${side.schema}.` : ''}${side.table} of ${side.connectionName} again from a Runner channel`}>Refresh</button>
        )}
      </div>
      {refreshing && side.connectionName && table && (
        <RefreshPanel key={`${side.connectionName}/${table.schema}.${table.name}`} connection={side.connectionName} table={table} onClose={() => setRefreshing(false)} testId={`cached-metadata-${id}-refresh`} />
      )}
    </div>
  )
}
