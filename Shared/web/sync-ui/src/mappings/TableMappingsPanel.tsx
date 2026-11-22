import { Link, Navigate } from '@dbdatakit/shell'
import { designerPath, useDraft } from '@dbdatakit/workspace'
import type { SyncDraft } from '../useSyncDraft'
import { MappingsOverview } from './MappingsOverview'
import { NewMappingForm, TableMappingForm } from './TableMappingForm'

/** Where an address after `mappings/` points: the overview, a new mapping, or a mapping and its tab. */
export function placeOfMappings(rest: string): { kind: 'overview' } | { kind: 'new' } | { kind: 'mapping'; name: string; tab: string } {
  if (rest === '' || rest === 'overview') return { kind: 'overview' }
  if (rest === 'new') return { kind: 'new' }
  const [name, tab] = rest.split('/')
  return { kind: 'mapping', name: decodeURIComponent(name), tab: tab ?? '' }
}

/**
 * The Table Mappings tab: a sidebar of the replication's mappings (each with its count of mapped columns, and **+** for a new one) and, beside it,
 * the overview of the source's tables or the open mapping's editor. Which one is open is in the address, so a link to a mapping can be sent.
 */
export function TableMappingsPanel({ project, id, replication, model, rest }: { project: string; id: string; replication: string; model: SyncDraft; rest: string }) {
  const draft = useDraft()
  const base = `replications/${replication}/mappings`
  const place = placeOfMappings(rest)
  const to = (r: string) => designerPath(project, id, `${base}/${r}`)

  return (
    <div className="row" style={{ alignItems: 'stretch', gap: 0, flex: 1 }}>
      <aside className="sidebar">
        <div className="sidebar-head">
          <span>Mappings</span>
          <Link to={to('new')} className="btn-link" data-testid="new-mapping-button" title="New mapping">+</Link>
        </div>
        <div className="sidebar-list" data-testid="mappings-sidebar">
          <Link to={to('overview')} className={`sidebar-item strong ${place.kind === 'overview' ? 'active' : ''}`} data-testid="mappings-overview-link">Overview</Link>
          {model.mappingNames.map((n) => {
            const m = model.mappingOf(n)
            const open = place.kind === 'mapping' && place.name === n
            return (
              <Link key={n} to={to(`${n}/columns`)} className={`sidebar-item ${open ? 'active' : ''}`} aria-current={open ? 'page' : undefined} data-testid={`mapping-item-${n}`}>
                <span className="sidebar-item-name" title={n}>{n}{m?.dirty ? ' •' : ''}</span>
                <span className="badge">{m?.value ? m.value.columnMappings.length : m?.problem ? '!' : '…'}</span>
              </Link>
            )
          })}
          {model.mappingNames.length === 0 && <span className="hint" style={{ padding: '6px 7px' }}>No mappings yet.</span>}
          {place.kind === 'new' && <span className="sidebar-item active">new mapping</span>}
        </div>
      </aside>

      <div className="pane">
        {place.kind === 'overview' && <MappingsOverview model={model} pathOf={(name) => to(`${name}/columns`)} />}
        {place.kind === 'new' && <NewMappingForm model={model} pathOf={(name) => to(`${name}/columns`)} cancelPath={to('overview')} />}
        {place.kind === 'mapping' && (model.mappingNames.includes(place.name)
          ? <TableMappingForm key={place.name} model={model} name={place.name} tab={place.tab} base={designerPath(project, id, `${base}/${place.name}`)} afterRemove={to('overview')} />
          : draft.filesLoading ? <div className="empty">Loading…</div> : <Navigate to={to('overview')} replace />)}
      </div>
    </div>
  )
}
