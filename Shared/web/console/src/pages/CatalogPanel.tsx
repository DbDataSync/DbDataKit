import { useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { readSource, writeSource } from '../viewsUrl'
import { discoverSources, pickSource, FIXTURES, type ViewsSource } from '../viewsSource'
import {
  CatalogError, TYPE_LABEL, afterChange, catalogClient, crumbs, documentAddress, fixtureCatalog, followable, framable, initialValues, missingRequired, openValues, readCatalogState, writeCatalogState,
  type CatalogClient, type CatalogDetail, type CatalogItem, type CatalogOpening, type CatalogParameter,
} from '../catalog'

const clientFor = (source: ViewsSource): CatalogClient => (source.id === FIXTURES ? fixtureCatalog() : catalogClient({ baseUrl: `/relay/${encodeURIComponent(source.id)}` }))

const words = (e: unknown) => (e instanceof CatalogError || e instanceof Error ? e.message : String(e))

/**
 * The one parameters pane of every catalog (plan 32 RC05, section 9e): a control per parameter of the item, whatever catalog it is from. A parameter with listed values is a choice (several for a `many` one), one without is typed; a parameter another depends on empties
 * its dependents when it changes (their listed values would not hold any more); one that is required and empty is named before anything is sent.
 */
export function ParametersPane({ parameters, values, onChange }: { parameters: CatalogParameter[]; values: Record<string, string[]>; onChange: (next: Record<string, string[]>) => void }) {
  if (parameters.length === 0) return <p className="hint" data-testid="catalog-params-none">This item has no parameters.</p>
  return (
    <div data-testid="catalog-params" className="stack" style={{ gap: 8 }}>
      {parameters.map((p) => {
        const id = `catalog-param-${p.name}`
        const current = values[p.name] ?? []
        const label = <label htmlFor={id}>{p.label || p.name}{p.required ? ' *' : ''}</label>
        return p.validValues && p.validValues.length > 0 ? (
          <div key={p.name}>
            {label}
            <select id={id} data-testid={id} multiple={p.many} value={p.many ? current : current[0] ?? ''} onChange={(e) => onChange(afterChange(parameters, values, p.name, p.many ? [...e.target.selectedOptions].map((o) => o.value) : e.target.value === '' ? [] : [e.target.value]))}>
              {!p.many && <option value="">{p.required ? 'Choose…' : 'Default'}</option>}
              {p.validValues.map((o) => <option key={o.value} value={o.value}>{o.label || o.value}</option>)}
            </select>
            {p.dependsOn && p.dependsOn.length > 0 && <span className="hint"> depends on {p.dependsOn.join(', ')}</span>}
          </div>
        ) : (
          <div key={p.name}>
            {label}
            <input id={id} data-testid={id} value={p.many ? current.join(',') : current[0] ?? ''} placeholder={p.many ? 'a, b, c' : ''} onChange={(e) => onChange(afterChange(parameters, values, p.name, e.target.value === '' ? [] : p.many ? e.target.value.split(',').map((v) => v.trim()) : [e.target.value]))} />
          </div>
        )
      })}
    </div>
  )
}

/** The opened item: our document links to its board in the Views area, an external one is framed (sandboxed, no referrer, no opener) or followed as a link. */
export function Opened({ opening, source, values }: { opening: CatalogOpening; source: string | null; values: Record<string, string[]> }) {
  const address = documentAddress(opening, source, values)
  return (
    <div className="card" data-testid="catalog-opened" data-mode={opening.mode}>
      <strong>{opening.title}</strong>
      {opening.mode === 'document' && address && <p><Link className="btn btn-primary" to={address} data-testid="catalog-document-link">Open the {opening.kind}</Link> <span className="hint">drawn as you, with the layout and the parameters you chose</span></p>}
      {opening.mode === 'frame' && framable(opening.url) && (
        <iframe title={opening.title} data-testid="catalog-frame" src={opening.url} sandbox="allow-scripts allow-same-origin allow-forms" referrerPolicy="no-referrer" style={{ width: '100%', height: 480, border: '1px solid var(--border, #ccc)' }} />
      )}
      {(opening.mode === 'link' || (opening.mode === 'frame' && !framable(opening.url))) && (
        followable(opening.url)
          ? <p><a className="btn btn-primary" href={opening.url} target="_blank" rel="noopener noreferrer" data-testid="catalog-link">Open in the report server</a> <span className="hint">it opens off this site, where the report server signs you in</span></p>
          : <p className="error" role="alert" data-testid="catalog-no-address">The catalog gave no address this page may follow.</p>
      )}
    </div>
  )
}

function Detail({ client, itemKey, source, values, setValues }: { client: CatalogClient; itemKey: string; source: string | null; values: Record<string, string[]> | null; setValues: (v: Record<string, string[]>) => void }) {
  const detail = useQuery<CatalogDetail>({ queryKey: ['catalog', 'item', source, itemKey], queryFn: ({ signal }) => client.item(itemKey, signal), retry: false })
  const [opening, setOpening] = useState<CatalogOpening | null>(null)
  const [exportLink, setExportLink] = useState<{ format: string; url: string } | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  if (detail.isPending) return <div className="empty" role="status" data-testid="catalog-detail-loading">Loading…</div>
  if (detail.isError) return <div className="empty" role="alert" data-testid="catalog-error">{words(detail.error)}</div>
  const { item, parameters, exports } = detail.data
  const current = values ?? initialValues(parameters, {})
  const run = async (key: string, format: string | null) => {
    const missing = missingRequired(parameters, current)
    if (missing.length > 0) { setProblem(`Choose ${missing.join(', ')} first.`); return }
    setBusy(key); setProblem(null)
    try {
      const o = await client.open(item.key, { parameters: openValues(parameters, current), format })
      if (format) { if (followable(o.url)) { setExportLink({ format, url: o.url }); setOpening(null) } else if (o.mode === 'document') setOpening(o) } else { setOpening(o); setExportLink(null) }
    } catch (e) { setProblem(words(e)) } finally { setBusy(null) }
  }
  return (
    <div className="card" data-testid="catalog-detail" data-key={item.key}>
      <h2 style={{ marginTop: 0 }}>{item.name} <span className="badge" data-testid="catalog-detail-provider">{item.provider}</span></h2>
      <p className="hint">{item.path}{item.description ? ` — ${item.description}` : ''}</p>
      <ParametersPane parameters={parameters} values={current} onChange={setValues} />
      <div className="row" style={{ gap: 8, marginTop: 12, flexWrap: 'wrap', alignItems: 'center' }}>
        <button type="button" className="btn btn-primary" data-testid="catalog-open" disabled={busy !== null} onClick={() => void run('open', null)}>{busy === 'open' ? 'Opening…' : 'Open'}</button>
        {exports.length > 0 && <span className="hint">Export as</span>}
        {exports.map((e) => (
          <button key={e.format} type="button" className="btn btn-sm" data-testid={`catalog-export-${e.format.toLowerCase()}`} disabled={busy !== null || !e.available} title={e.reason ?? undefined} onClick={() => void run(e.format, e.format)}>{e.format}</button>
        ))}
      </div>
      {problem && <p className="error" role="alert" data-testid="catalog-problem">{problem}</p>}
      {exportLink && <p><a href={exportLink.url} target="_blank" rel="noopener noreferrer" data-testid="catalog-export-link">Download the {exportLink.format} file</a> <span className="hint">the report server renders it and signs you in</span></p>}
      {opening && <Opened opening={opening} source={source === FIXTURES ? null : source} values={current} />}
    </div>
  )
}

/**
 * The report browser (plan 32 RC05, section 9e): one list over our own dashboards and reports and the external report servers' catalogs, folders and search, each item marked with its catalog, a catalog that does not answer named beside the others' items. An item opens in a detail
 * with its parameters pane, **Open** (ours in its board; external in a frame when the instance approves the report server's origin, else a link off this site) and an export in a format it offers. The source is a Gateway that runs the views module with catalogs, or the sample catalogs.
 */
export function CatalogPanel() {
  const [search, setSearch] = useSearchParams()
  const state = readCatalogState(search)
  const sources = useQuery({ queryKey: ['catalog', 'sources'], queryFn: () => discoverSources(), retry: false })
  const picked = pickSource((sources.data ?? []).filter((s) => !s.personal), readSource(search))
  const source = picked.source
  const client = clientFor(source)
  const [typed, setTyped] = useState(state.search)
  const [values, setValuesState] = useState<{ key: string; values: Record<string, string[]> } | null>(null)
  const listing = useQuery({ queryKey: ['catalog', 'browse', source.id, state.folder, state.search], queryFn: ({ signal }) => client.browse({ folder: state.folder, search: state.search || null }, signal), retry: false })
  const go = (next: Partial<ReturnType<typeof readCatalogState>>) => setSearch(writeCatalogState(search, next))
  const submit = (e: FormEvent) => { e.preventDefault(); go({ search: typed.trim(), item: null }) }
  const detailValues = values && values.key === state.item ? values.values : Object.keys(state.values).length > 0 ? state.values : null

  return (
    <div data-testid="catalog-panel">
      <div className="card" style={{ marginBottom: 16 }}>
        <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
          <label htmlFor="catalog-source">Source</label>
          <select id="catalog-source" data-testid="catalog-source" value={source.id} onChange={(e) => setSearch(writeSource(new URLSearchParams(), e.target.value))}>
            {(sources.data ?? [{ id: FIXTURES, label: 'Sample catalogs', published: false } as ViewsSource]).filter((s) => !s.personal).map((s) => <option key={s.id} value={s.id}>{s.id === FIXTURES ? 'Sample catalogs' : s.label.replace(' (published)', '')}</option>)}
          </select>
          <form onSubmit={submit} className="row" style={{ gap: 8, alignItems: 'center' }}>
            <input aria-label="Search the catalogs" data-testid="catalog-search" value={typed} placeholder="Search by name, folder or description" onChange={(e) => setTyped(e.target.value)} />
            <button type="submit" className="btn" data-testid="catalog-search-go">Search</button>
            {state.search && <button type="button" className="btn btn-sm" data-testid="catalog-search-clear" onClick={() => { setTyped(''); go({ search: '', item: null }) }}>Clear</button>}
          </form>
        </div>
        {picked.unknown && <p className="hint" role="note" data-testid="catalog-source-unknown">`{picked.unknown}` is not a source now; showing {source.id === FIXTURES ? 'the sample catalogs' : source.label}.</p>}
        {!state.search && (
          <nav className="row" style={{ gap: 4, marginTop: 8 }} aria-label="Folders" data-testid="catalog-crumbs">
            <button type="button" className="btn btn-sm" data-testid="catalog-crumb-/" onClick={() => go({ folder: '/', item: null })}>All catalogs</button>
            {crumbs(state.folder).map((c) => <button key={c.path} type="button" className="btn btn-sm" data-testid={`catalog-crumb-${c.path}`} onClick={() => go({ folder: c.path, item: null })}>{c.name}</button>)}
          </nav>
        )}
      </div>
      {listing.isPending && <div className="empty" role="status" data-testid="catalog-loading">Loading the catalogs…</div>}
      {listing.isError && (
        <div className="empty" role="alert" data-testid="catalog-error">
          {words(listing.error)}
          {source.id !== FIXTURES && <> <button type="button" className="btn btn-sm" onClick={() => setSearch(writeSource(new URLSearchParams(), FIXTURES))} data-testid="catalog-use-samples">Show the sample catalogs</button></>}
        </div>
      )}
      {listing.data?.unavailable.map((p) => <div key={p.provider} className="empty" role="alert" data-testid={`catalog-unavailable-${p.provider}`}>The catalog <strong>{p.provider}</strong> did not answer, so its items are not listed: {p.message}</div>)}
      {listing.data && listing.data.items.length === 0 && <div className="empty" data-testid="catalog-empty">{state.search ? `Nothing matches "${state.search}" that you may see.` : 'Nothing here that you may see.'}</div>}
      {listing.data && listing.data.items.length > 0 && (
        <table className="table" data-testid="catalog-list">
          <thead><tr><th>Name</th><th>Catalog</th><th>Kind</th><th>Where</th></tr></thead>
          <tbody>
            {listing.data.items.map((i: CatalogItem) => (
              <tr key={i.key} data-testid={`catalog-row-${i.key}`} data-type={i.type}>
                <td>
                  <button type="button" className="link" data-testid={`catalog-open-${i.key}`} onClick={() => (i.type === 'folder' ? go({ folder: i.path, search: '', item: null }) : go({ item: i.key, values: {} }))}>{i.type === 'folder' ? `${i.name}/` : i.name}</button>
                </td>
                <td><span className="badge" data-testid={`catalog-provider-${i.key}`}>{i.provider}</span></td>
                <td>{TYPE_LABEL[i.type] ?? i.type}</td>
                <td>{i.path}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {state.item && <Detail key={`${source.id}|${state.item}`} client={client} itemKey={state.item} source={source.id} values={detailValues} setValues={(v) => { setValuesState({ key: state.item!, values: v }); setSearch(writeCatalogState(search, { values: v }), { replace: true }) }} />}
      {listing.data && Object.keys(listing.data.creatable).length > 0 && <p className="hint" data-testid="catalog-creatable">New items can be made in: {Object.entries(listing.data.creatable).map(([p, k]) => `${p} (${k.join(', ')})`).join('; ')}.</p>}
    </div>
  )
}
