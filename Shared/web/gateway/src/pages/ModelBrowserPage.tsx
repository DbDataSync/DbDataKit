import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ErrorBanner } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import { GatewayShell } from '../GatewayShell'
import { bindingLine, filterCatalog, lineageLink, measureLine } from '../modelBrowser'
import type { ModelVersionRow, SemanticCatalogView } from '../types'

const wrap = { whiteSpace: 'normal', overflowWrap: 'anywhere' } as const
const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '-')

export interface ModelBrowserViewProps {
  view: SemanticCatalogView | undefined
  loading: boolean
  error: unknown
  /** The versions, when the person may read them (an administrator or the publisher); undefined otherwise. */
  versions?: ModelVersionRow[]
  search: string
  onSearch: (text: string) => void
}

/** A model's content as its catalog says it: dimensions with their attributes and the column each is read from, measures, hierarchies, calendars, segments, saved queries and (when allowed) the versions. A viewer: the model is authored in Build. */
export function ModelBrowserView(p: ModelBrowserViewProps) {
  const view = p.view
  const found = view ? filterCatalog(view, p.search) : undefined
  return (
    <div className="pane" data-testid="model-browser">
      <ErrorBanner error={p.error} />
      {p.loading && <div className="empty">Loading…</div>}
      {view && found && (
        <>
          <div className="page-head">
            <h1 className="page-title mono" data-testid="model-name">{view.name}</h1>
            <span className="page-note" data-testid="model-version">{view.published ? `version ${view.version} (published)` : 'set up in server.yaml'} · time zone {view.timezone}. Authored in Build; this page only reads it.</span>
          </div>
          <input value={p.search} onChange={(e) => p.onSearch(e.target.value)} placeholder="find a dimension, an attribute or a measure" aria-label="Find" data-testid="model-search" style={{ minWidth: 300, marginBottom: 10 }} />

          <h2 className="card-title">Measures</h2>
          <div className="card flush" data-testid="model-measures">
            <div className="grid-head" style={{ gridTemplateColumns: '1.2fr 0.7fr 2.6fr 1.4fr', gap: 14 }}><span>Measure</span><span>Type</span><span>Computed as</span><span>Lineage</span></div>
            {found.measures.length === 0 && <div className="empty" data-testid="model-no-measures">No measure matches.</div>}
            {found.measures.map((m) => (
              <div key={m.name} className="grid-row auto" style={{ gridTemplateColumns: '1.2fr 0.7fr 2.6fr 1.4fr', gap: 14 }} data-testid={`measure-${m.name}`}>
                <span className="mono">{m.name}{m.synonyms.length > 0 && <div className="hint">also: {m.synonyms.join(', ')}</div>}</span>
                <span>{m.type}</span>
                <span style={wrap}>{measureLine(m)}{m.filtered && <div className="hint">with filters of its own</div>}</span>
                <span><Link to={lineageLink(view.name, m.name)} data-testid={`lineage-${m.name}`}>Follow to the Build column</Link></span>
              </div>
            ))}
          </div>

          <h2 className="card-title" style={{ marginTop: 20 }}>Dimensions</h2>
          {found.dimensions.length === 0 && <div className="empty" data-testid="model-no-dimensions">No dimension matches.</div>}
          {found.dimensions.map((d) => (
            <div key={d.name} className="card flush" style={{ marginBottom: 10 }} data-testid={`dimension-${d.name}`}>
              <div className="grid-head" style={{ gridTemplateColumns: '1.4fr 0.7fr 2.6fr 1.4fr', gap: 14 }}><span>{d.label ?? d.name} <span className="hint">({d.name})</span></span><span>Type</span><span>Read from</span><span>Lineage</span></div>
              {d.attributes.map((a) => (
                <div key={a.name} className="grid-row auto" style={{ gridTemplateColumns: '1.4fr 0.7fr 2.6fr 1.4fr', gap: 14 }} data-testid={`attribute-${a.name}`}>
                  <span className="mono">{a.name}{a.synonyms.length > 0 && <div className="hint">also: {a.synonyms.join(', ')}</div>}</span>
                  <span>{a.type}</span>
                  <span style={wrap} className="mono">{bindingLine(a)}</span>
                  <span>{a.bindings.length > 0 && <Link to={lineageLink(view.name, a.name)} data-testid={`lineage-${a.name}`}>Follow to the Build column</Link>}</span>
                </div>
              ))}
            </div>
          ))}

          {view.hierarchies.length > 0 && (
            <section data-testid="model-hierarchies">
              <h2 className="card-title" style={{ marginTop: 20 }}>Hierarchies</h2>
              {view.hierarchies.map((h) => <div key={h.name} className="hint" data-testid={`hierarchy-${h.name}`}><span className="mono">{h.name}</span>: {h.levels.join(' > ')}</div>)}
            </section>
          )}
          {view.calendars.length > 0 && (
            <section data-testid="model-calendars">
              <h2 className="card-title" style={{ marginTop: 20 }}>Calendars</h2>
              {view.calendars.map((c) => <div key={c.name} className="hint" data-testid={`calendar-${c.name}`}><span className="mono">{c.name}</span> on {c.date}: {Object.keys(c.levels).join(', ')}</div>)}
            </section>
          )}
          {view.segments.length > 0 && <div className="hint" style={{ marginTop: 12 }} data-testid="model-segments">Segments: <span className="mono">{view.segments.join(', ')}</span></div>}
          {view.queries.length > 0 && (
            <section data-testid="model-queries">
              <h2 className="card-title" style={{ marginTop: 20 }}>Saved queries of the model</h2>
              {view.queries.map((q) => (
                <div key={q.name} style={{ marginBottom: 8 }} data-testid={`query-${q.name}`}>
                  <span className="mono">{q.name}</span> <span className="hint">{q.description ?? ''}{Object.keys(q.parameters).length > 0 ? ` · parameters: ${Object.keys(q.parameters).join(', ')}` : ''}</span>
                  {q.sql && <pre className="mono" style={wrap}>{q.sql}</pre>}
                </div>
              ))}
            </section>
          )}
          <h2 className="card-title" style={{ marginTop: 20 }}>Physical sources</h2>
          <div className="hint" data-testid="model-sources">{view.sources.map((s) => `${s.name} (${s.kind}): fact ${s.fact_table}${s.joins.length > 0 ? `, joins ${s.joins.join(', ')}` : ''}`).join('; ') || 'none'}</div>

          {p.versions && (
            <section data-testid="model-versions">
              <h2 className="card-title" style={{ marginTop: 20 }}>Versions</h2>
              {[...p.versions].sort((a, b) => b.version - a.version).map((v) => (
                <div key={v.version} className="hint" data-testid={`model-version-${v.version}`}>{v.version}{v.version === view.version ? ' (served)' : ''} · {v.published_by ?? '-'} · {when(v.published_at)}</div>
              ))}
            </section>
          )}
        </>
      )}
    </div>
  )
}

/** `/models/<name>`: the model browser of this Gateway (plan 14a SL13), reached from the Models screen. */
export function ModelBrowserPage() {
  const { name = '' } = useParams()
  const [search, setSearch] = useState('')
  const catalog = useQuery({ queryKey: ['gateway', 'models', 'catalog', name], queryFn: () => gatewayApi.models.semantic.catalog(name), refetchOnMount: 'always' })
  // the versions are the publisher's and an administrator's to read: a viewer's refusal only leaves them out
  const detail = useQuery({ queryKey: ['gateway', 'models', 'semantic', name], queryFn: () => gatewayApi.models.semantic.read(name), retry: false, refetchOnMount: 'always' })
  return (
    <GatewayShell crumbs={[{ label: 'Models', to: '/models' }, { label: name, mono: true, heading: true }]}>
      <ModelBrowserView view={catalog.data} loading={catalog.isLoading} error={catalog.error} versions={detail.data?.versions} search={search} onSearch={setSearch} />
    </GatewayShell>
  )
}
