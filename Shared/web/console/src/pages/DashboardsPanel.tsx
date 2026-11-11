import { useEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { DashboardView, checkDrill, type ClientDiagnostic, type ResolvedDrill, type ViewKind, type ViewsDoc } from '@dbdatakit/visuals'
import { crumbAddress, describeValue, drillAddress, readKiosk, readLayout, readParameters, readSource, readTrail, withSource, writeKiosk, writeLayout, writeParameters, writeSource } from '../viewsUrl'
import { discoverSources, personalFor, pickSource, resolveLog, viewsClient, type ViewsSource } from '../viewsSource'
import { isShared } from '../userViews'
import { DashboardEditor } from './DashboardEditor'
import { VersionsPanel } from './VersionsPanel'
import { MyViewsPanel, PinPanel, SharedBy } from './MyViewsPanel'

const AREA: Record<ViewKind, { path: string; plural: string; singular: string }> = {
  dashboard: { path: '/views/dashboards', plural: 'Dashboards', singular: 'dashboard' },
  report: { path: '/views/reports', plural: 'Reports', singular: 'report' },
}

const where = (d: ClientDiagnostic) => [d.file, d.line, d.column].filter((p) => p !== undefined).join(':')

function Problems({ diagnostics }: { diagnostics: ClientDiagnostic[] }) {
  return (
    <ul data-testid="views-problems">
      {diagnostics.map((d, i) => (
        <li key={i}><code>{where(d) || d.path}</code> {d.code} {d.message}</li>
      ))}
    </ul>
  )
}

/** The file a person opens: checked as the loaders check it, then kept in the fixture source for this visit under the file's name, and opened. */
async function openFile(file: File): Promise<{ kind: ViewKind; name: string } | { problems: ClientDiagnostic[] }> {
  const client = viewsClient()   // a file is opened in the fixture source: it is kept for the visit, never published
  const text = await file.text()
  const name = file.name.replace(/\.(ya?ml|json)$/i, '')
  const checked = await client.validate(text, { file: file.name })
  if (!checked.ok || !checked.document) return { problems: checked.diagnostics }
  const kind = checked.document.kind
  const existing = (await client.list(kind)).some((d) => d.name === name) ? (await client.open(kind, name)).version : undefined
  await client.save(kind, name, text, existing)
  return { kind, name }
}

/** Where the documents come from: the fixtures, or a Gateway's published ones; a change of source is a different list, so it goes back to the list. */
export function SourcePicker({ sources, source, unknown, kind }: { sources: ViewsSource[]; source: ViewsSource; unknown: string | null; kind: ViewKind }) {
  const navigate = useNavigate()
  return (
    <div className="row" style={{ gap: 8, alignItems: 'center', marginBottom: 8 }} data-testid="views-source-picker">
      <label htmlFor={`views-source-${kind}`}>Source</label>
      <select id={`views-source-${kind}`} data-testid="views-source" value={source.id} onChange={(e) => navigate(`${AREA[kind].path}?${writeSource(new URLSearchParams(), e.target.value).toString()}`)}>
        {sources.map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
      </select>
      <span className="hint" data-testid="views-source-current" data-source={source.id} data-published={source.published ? 'true' : 'false'}>
        {source.personal ? 'Your own documents, and the ones others shared with you: every widget is read as you, so a shared document shows you the rows your own access gives.' : source.published ? 'Published to the group: every widget is read as you.' : sources.some((s) => s.published) ? 'The sample files and their recorded results; nothing is sent anywhere.' : 'No Gateway runs the views module, so these are the sample files and their recorded results.'}
      </span>
      {unknown && <span className="hint" role="note" data-testid="views-source-unknown">`{unknown}` is not a source now (it has no views module, or it did not answer); showing {source.label}.</span>}
    </div>
  )
}

function Listing({ kind, source, sources, unknown, keep }: { kind: ViewKind; source: ViewsSource; sources: ViewsSource[]; unknown: string | null; keep: string | null }) {
  const area = AREA[kind]
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const list = useQuery({ queryKey: ['views', 'list', source.id, kind], queryFn: () => viewsClient(source.id).list(kind), retry: false })
  const [problems, setProblems] = useState<ClientDiagnostic[] | null>(null)
  const [fileName, setFileName] = useState<string | null>(null)
  return (
    <>
      <div className="card" style={{ marginBottom: 16 }}>
        <SourcePicker sources={sources} source={source} unknown={unknown} kind={kind} />
        <div className="row" style={{ gap: 8, alignItems: 'center' }}>
          {!source.published && !source.personal && <label className="btn" htmlFor={`views-open-file-${kind}`}>Open file</label>}
          {!source.published && !source.personal && <input
            id={`views-open-file-${kind}`}
            type="file"
            accept=".yaml,.yml,.json"
            data-testid="views-open-file"
            style={{ display: 'none' }}
            onChange={(e) => {
              const file = e.target.files?.[0]
              e.target.value = ''
              if (!file) return
              setFileName(file.name)
              void openFile(file).then(async (result) => {
                if ('problems' in result) { setProblems(result.problems); return }
                setProblems(null)
                await queryClient.invalidateQueries({ queryKey: ['views', 'list'] })
                navigate(withSource(`${AREA[result.kind].path}/${encodeURIComponent(result.name)}`, keep))
              })
            }}
          />}
          <Link className="btn btn-primary" to={withSource(`${area.path}?new=1`, keep)} data-testid="views-new">New {area.singular}</Link>
          <span className="hint">
            {source.published
              ? `Saving a ${area.singular} publishes it to the group (it needs the author role); a person sees the rows their own access gives.`
              : `A ${area.singular} file (YAML or JSON). Its results come from the recorded fixtures; nothing is sent anywhere.`}
          </span>
        </div>
        {problems && (
          <div className="empty" data-testid="views-file-refused" role="alert">
            <strong>{fileName} is not shown: it has errors.</strong>
            <Problems diagnostics={problems} />
          </div>
        )}
      </div>
      {list.isPending && <div className="empty" data-testid="views-loading" role="status">Loading {area.plural.toLowerCase()}…</div>}
      {list.isError && <div className="empty" data-testid="views-error" role="alert">The {area.plural.toLowerCase()} could not be listed: {String(list.error)}</div>}
      {list.data && list.data.length === 0 && (
        <div className="empty" data-testid="views-empty">
          {source.published
            ? `No ${area.plural.toLowerCase()} are published to this group yet, or none you may open. Publish one with \`dbdatagateway views publish --file <document>\`, or with New ${area.singular} here.`
            : `No ${area.plural.toLowerCase()} in the fixture source. Open a file to see one.`}
        </div>
      )}
      {list.data && list.data.length > 0 && (
        <table className="table" data-testid="views-list">
          <thead><tr><th>Name</th><th>Title</th></tr></thead>
          <tbody>
            {list.data.map((d) => (
              <tr key={d.name} data-testid={`views-row-${d.name}`}>
                <td><Link to={withSource(`${area.path}/${encodeURIComponent(d.name)}`, keep)} data-testid={`views-open-${d.name}`}>{d.name}</Link></td>
                <td>{d.title}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}

/** The board with no chrome of the console (`?mode=kiosk`): the whole window, and in a corner the way out (Escape too) and the way back where a drill came from. */
function KioskFrame({ children, search, setSearch }: { children: ReactNode; search: URLSearchParams; setSearch: (next: URLSearchParams) => void }) {
  const navigate = useNavigate()
  const trail = readTrail(search)
  const exit = () => setSearch(writeKiosk(search, false))
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') exit() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })
  return (
    <div className="vz-kiosk" data-testid="views-kiosk-page">
      {children}
      <div className="vz-kiosk-controls">
        {trail.length > 0 && <button type="button" className="btn btn-sm" data-testid="views-kiosk-back" onClick={() => navigate(crumbAddress(trail, trail.length - 1))}>Back</button>}
        <button type="button" className="btn btn-sm" data-testid="views-kiosk-exit" onClick={exit}>Exit kiosk</button>
      </div>
    </div>
  )
}

function Board({ kind, name, document, source, keep, personal }: { kind: ViewKind; name: string; document: ViewsDoc; source: ViewsSource; keep: string | null; personal: ViewsSource | null }) {
  const [search, setSearch] = useSearchParams()
  const navigate = useNavigate()
  const kiosk = readKiosk(search)
  const trail = readTrail(search)
  const [drillNotice, setDrillNotice] = useState<string | null>(null)
  const { layout, unknown } = readLayout(search, document.layouts)
  const searchKey = search.toString()
  // the values are the address's: the same address is the same values, which is what the view resolves by
  const parameters = useMemo(() => readParameters(search, document.parameters), [searchKey, document]) // eslint-disable-line react-hooks/exhaustive-deps
  const defaults = useMemo(() => readParameters(new URLSearchParams(), document.parameters), [document])
  const [shown, setShown] = useState<string | null>(null)
  const [showVersions, setShowVersions] = useState(false)
  const log = useSyncExternalStore(resolveLog.subscribe, resolveLog.snapshot)
  const last = log[log.length - 1]
  const atDefaults = JSON.stringify(parameters) === JSON.stringify(defaults)

  /** A drill: the target is opened (its own parameters are the ones the drill is checked against), and the address that carries the values and the way back is gone to. */
  const drill = async (d: ResolvedDrill) => {
    setDrillNotice(null)
    if (d.kind === 'view') { setDrillNotice(`The drill goes to the view \`${d.name}\`; views are opened from the Rendered views tab, which a drill does not reach yet.`); return }
    let target
    try {
      target = await viewsClient(source.id).open(d.kind, d.name)
    } catch (e) {
      setDrillNotice(`The drill goes to the ${d.kind} \`${d.name}\`, which could not be opened: ${e instanceof Error ? e.message : String(e)}`)
      return
    }
    if (!target.document) { setDrillNotice(`The drill goes to the ${d.kind} \`${d.name}\`, which has errors and is not shown.`); return }
    const checked = checkDrill(d, target.document.parameters)
    if (!checked.ok) { setDrillNotice(checked.message); return }
    navigate(drillAddress({ drill: { kind: d.kind, name: d.name, parameters: d.parameters }, targetParameters: target.document.parameters, here: { kind, name, search } }))
  }
  const view = (
    <DashboardView
      kind={kind}
      name={name}
      document={document}
      client={viewsClient(source.id)}
      layout={layout ?? undefined}
      theme="auto"
      kiosk={kiosk}
      parameters={parameters}
      onParameterChange={(parameter, value) => setSearch(writeParameters(search, document.parameters, { ...parameters, [parameter]: value }), { replace: true })}
      onSelect={(selection) => setShown(selection.layout.name)}
      onDrill={(d) => void drill(d)}
      sortable   // headers sort on screen, and on the server when it cut a table (DBR45); print and export use the document's own order
    />
  )
  if (kiosk)
    return (
      <KioskFrame search={search} setSearch={(next) => setSearch(next, { replace: true })}>
        <div className="vz-kiosk-stage" data-testid="views-stage">{view}</div>
        {drillNotice && <p className="vz-interaction-notice" role="alert" data-testid="views-drill-refused">{drillNotice}</p>}
      </KioskFrame>
    )

  return (
    <>
      {trail.length > 0 && (
        <nav className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap', marginBottom: 8 }} aria-label="Where this drill came from" data-testid="views-trail">
          <button type="button" className="btn btn-sm" data-testid="views-trail-back" onClick={() => navigate(crumbAddress(trail, trail.length - 1))}>Back</button>
          {trail.map((c, i) => (
            <span key={i}><Link to={crumbAddress(trail, i)} data-testid={`views-trail-${i}`}>{c.name}</Link> / </span>
          ))}
          <strong data-testid="views-trail-here">{document.title}</strong>
        </nav>
      )}
      <div className="row" style={{ gap: 12, alignItems: 'center', flexWrap: 'wrap' }} data-testid="views-toolbar">
        <Link to={withSource(AREA[kind].path, keep)} data-testid="views-back">All {AREA[kind].plural.toLowerCase()}</Link>
        <strong data-testid="views-title">{document.title}</strong>
        <span className="hint" data-testid="views-source-current" data-source={source.id} data-published={source.published ? 'true' : 'false'}>{source.published ? source.label : 'Fixtures'}</span>
        {isShared(name) && <SharedBy source={source} kind={kind} name={name} />}
        {!isShared(name) && <Link to={withSource('?edit=1', keep)} className="btn btn-sm" data-testid="views-edit">Edit</Link>}
        {source.published && <button type="button" className="btn btn-sm" data-testid="views-versions" aria-expanded={showVersions} title="Who published each version, serve an earlier one, or remove it from the group" onClick={() => setShowVersions(!showVersions)}>Versions</button>}
        {personal && <PinPanel document={document} personal={personal} />}
        <button type="button" className="btn btn-sm" data-testid="views-kiosk" title="Show this board alone, filling the window" onClick={() => setSearch(writeKiosk(search, true))}>Kiosk</button>
        <label htmlFor="views-layout-select">Layout</label>
        <select id="views-layout-select" data-testid="views-layout-select" value={layout ?? ''} onChange={(e) => setSearch(writeLayout(search, e.target.value === '' ? null : e.target.value), { replace: true })}>
          <option value="">Automatic (by window size)</option>
          {document.layouts.map((l) => <option key={l.name} value={l.name}>{l.name} ({l.type})</option>)}
        </select>
        <span className="hint" data-testid="views-layout-current" data-layout={shown ?? ''}>{shown ? `Showing ${shown}` : ''}</span>
        {(document.parameters ?? []).length > 0 && (
          <span className="hint" data-testid="views-parameters">
            {(document.parameters ?? []).map((p) => <span key={p.name} data-testid={`views-parameter-${p.name}`} style={{ marginRight: 8 }}>{p.label ?? p.name}: {describeValue(parameters[p.name])}</span>)}
            <button type="button" className="btn btn-sm" data-testid="views-parameters-reset" disabled={atDefaults} onClick={() => setSearch(writeParameters(search, document.parameters, defaults), { replace: true })}>Reset</button>
          </span>
        )}
        <span className="hint" data-testid="views-resolved" data-count={log.length} data-widgets={last ? last.widgets.join(',') : ''}>
          {last ? `Last request: ${last.widgets.length} widget${last.widgets.length === 1 ? '' : 's'} (${last.widgets.join(', ')})` : 'Nothing requested yet'}
        </span>
      </div>
      {source.published && showVersions && <VersionsPanel kind={kind} name={name} source={source.id} keep={keep} listPath={AREA[kind].path} />}
      {drillNotice && <div className="empty" data-testid="views-drill-refused" role="alert">{drillNotice} <button type="button" className="btn btn-sm" onClick={() => setDrillNotice(null)}>Dismiss</button></div>}
      {unknown && <div className="empty" data-testid="views-layout-unknown" role="note">This {kind} has no layout named `{unknown}`; the window decides instead. Its layouts: {document.layouts.map((l) => l.name).join(', ')}.</div>}
      {/* the stage fills what the page has left: the view follows its size, so the document never scrolls */}
      <div style={{ flex: 1, minHeight: 0, position: 'relative' }} data-testid="views-stage">
        <div style={{ position: 'absolute', inset: 0 }}>{view}</div>
      </div>
    </>
  )
}

function Opened({ kind, name, source, keep, personal }: { kind: ViewKind; name: string; source: ViewsSource; keep: string | null; personal: ViewsSource | null }) {
  const area = AREA[kind]
  const [search, setSearch] = useSearchParams()
  // in kiosk the page has no chrome, so what is not the board (opening, an error) still has the way out
  const wrap = (node: ReactNode) => (readKiosk(search) ? <KioskFrame search={search} setSearch={(next) => setSearch(next, { replace: true })}>{node}</KioskFrame> : node)
  const opened = useQuery({ queryKey: ['views', 'open', source.id, kind, name], queryFn: () => viewsClient(source.id).open(kind, name), retry: false })
  if (opened.isPending) return wrap(<div className="empty" data-testid="views-loading" role="status">Opening {name}…</div>)
  if (opened.isError) {
    return wrap(
      <div className="empty" data-testid="views-error" role="alert">
        {opened.error instanceof Error ? opened.error.message : String(opened.error)} <Link to={withSource(area.path, keep)}>All {area.plural.toLowerCase()}</Link>
      </div>,
    )
  }
  const { document, diagnostics } = opened.data
  if (!document) {
    return wrap(
      <div className="empty" data-testid="views-refused" role="alert">
        <strong>The {area.singular} {name} is not shown: it has errors.</strong>
        <Problems diagnostics={diagnostics} />
        <Link to={withSource(area.path, keep)}>All {area.plural.toLowerCase()}</Link>
      </div>,
    )
  }
  return <Board key={`${source.id}/${name}`} kind={kind} name={name} document={document} source={source} keep={keep} personal={personal} />
}

/**
 * The Dashboards and Reports tabs of the Views page (plan 32 DBR23, M1): the documents of the fixture source (and any file opened), and one document drawn with `DashboardView` from the shared visuals:
 * the layout the window selects or the one picked (`?layout=`), parameters kept in the address (`?p.region=West`) so a link opens the same board, only the widgets that use a changed parameter resolved again.
 * A report is the same board over its `flow` layout (DBR14): a scrolling paged sheet with page-break guides, Print, and a Print preview that stays disabled until the exporter is wired (DBR15); a dashboard's
 * `print` layout is the same view (`?layout=print`). Canvas layouts are drawn (DBR07). The source is the fixtures' recorded results, or the documents published to a Gateway that runs the `views` module (DBR11, DBR11b): `?source=` names it, and with none named
 * the first such Gateway is used, else the fixtures.
 */
export function DashboardsPanel({ kind }: { kind: ViewKind }) {
  const { name } = useParams()
  const [search] = useSearchParams()
  const sources = useQuery({ queryKey: ['views-sources'], queryFn: () => discoverSources(), staleTime: 30_000, retry: false })
  if (sources.isPending) return <div className="empty" data-testid="views-loading" role="status">Looking for the sources of {AREA[kind].plural.toLowerCase()}…</div>
  const requested = readSource(search)
  const { source, unknown } = pickSource(sources.data ?? [], requested)
  // a source the person chose stays in the address; the automatic choice does not, so a link made without one follows whatever is available
  const keep = requested && !unknown ? requested : null
  if (!name && search.get('new')) return <DashboardEditor key={`new-${source.id}`} kind={kind} name={null} source={source.id} keep={keep} />
  if (name && search.get('edit')) return <DashboardEditor key={`${source.id}/${name}`} kind={kind} name={decodeURIComponent(name)} source={source.id} keep={keep} />
  // a person's own documents (plan 32 DBR12): the personal source of the same server, which a widget is pinned to
  const personal = personalFor(sources.data ?? [], source)
  if (!name && source.personal) return <MyViewsPanel kind={kind} source={source} keep={keep} picker={<SourcePicker sources={sources.data ?? []} source={source} unknown={unknown} kind={kind} />} />
  return name
    ? <Opened kind={kind} name={decodeURIComponent(name)} source={source} keep={keep} personal={personal} />
    : <Listing kind={kind} source={source} sources={sources.data ?? []} unknown={unknown} keep={keep} />
}
