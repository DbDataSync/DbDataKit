import { useMemo, useState, useSyncExternalStore } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { DashboardView, type ClientDiagnostic, type ViewKind, type ViewsDoc } from '@dbdatakit/visuals'
import { describeValue, readLayout, readParameters, writeLayout, writeParameters } from '../viewsUrl'
import { resolveLog, viewsClient } from '../viewsSource'

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
  const client = viewsClient()
  const text = await file.text()
  const name = file.name.replace(/\.(ya?ml|json)$/i, '')
  const checked = await client.validate(text, { file: file.name })
  if (!checked.ok || !checked.document) return { problems: checked.diagnostics }
  const kind = checked.document.kind
  const existing = (await client.list(kind)).some((d) => d.name === name) ? (await client.open(kind, name)).version : undefined
  await client.save(kind, name, text, existing)
  return { kind, name }
}

function Listing({ kind }: { kind: ViewKind }) {
  const area = AREA[kind]
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const list = useQuery({ queryKey: ['views', 'list', kind], queryFn: () => viewsClient().list(kind) })
  const [problems, setProblems] = useState<ClientDiagnostic[] | null>(null)
  const [fileName, setFileName] = useState<string | null>(null)
  return (
    <>
      <div className="card" style={{ marginBottom: 16 }}>
        <div className="row" style={{ gap: 8, alignItems: 'center' }}>
          <label className="btn" htmlFor={`views-open-file-${kind}`}>Open file</label>
          <input
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
                navigate(`${AREA[result.kind].path}/${encodeURIComponent(result.name)}`)
              })
            }}
          />
          <span className="hint">A {area.singular} file (YAML or JSON). Its results come from the recorded fixtures; nothing is sent anywhere.</span>
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
      {list.data && list.data.length === 0 && <div className="empty" data-testid="views-empty">No {area.plural.toLowerCase()} in the fixture source. Open a file to see one.</div>}
      {list.data && list.data.length > 0 && (
        <table className="table" data-testid="views-list">
          <thead><tr><th>Name</th><th>Title</th></tr></thead>
          <tbody>
            {list.data.map((d) => (
              <tr key={d.name} data-testid={`views-row-${d.name}`}>
                <td><Link to={`${area.path}/${encodeURIComponent(d.name)}`} data-testid={`views-open-${d.name}`}>{d.name}</Link></td>
                <td>{d.title}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}

function Board({ kind, name, document }: { kind: ViewKind; name: string; document: ViewsDoc }) {
  const [search, setSearch] = useSearchParams()
  const { layout, unknown } = readLayout(search, document.layouts)
  const searchKey = search.toString()
  // the values are the address's: the same address is the same values, which is what the view resolves by
  const parameters = useMemo(() => readParameters(search, document.parameters), [searchKey, document]) // eslint-disable-line react-hooks/exhaustive-deps
  const defaults = useMemo(() => readParameters(new URLSearchParams(), document.parameters), [document])
  const [shown, setShown] = useState<string | null>(null)
  const log = useSyncExternalStore(resolveLog.subscribe, resolveLog.snapshot)
  const last = log[log.length - 1]
  const atDefaults = JSON.stringify(parameters) === JSON.stringify(defaults)

  return (
    <>
      <div className="row" style={{ gap: 12, alignItems: 'center', flexWrap: 'wrap' }} data-testid="views-toolbar">
        <Link to={AREA[kind].path} data-testid="views-back">All {AREA[kind].plural.toLowerCase()}</Link>
        <strong data-testid="views-title">{document.title}</strong>
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
      {unknown && <div className="empty" data-testid="views-layout-unknown" role="note">This {kind} has no layout named `{unknown}`; the window decides instead. Its layouts: {document.layouts.map((l) => l.name).join(', ')}.</div>}
      {/* the stage fills what the page has left: the view follows its size, so the document never scrolls */}
      <div style={{ flex: 1, minHeight: 0, position: 'relative' }} data-testid="views-stage">
        <div style={{ position: 'absolute', inset: 0 }}>
          <DashboardView
            kind={kind}
            name={name}
            document={document}
            client={viewsClient()}
            layout={layout ?? undefined}
            theme="auto"
            parameters={parameters}
            onParameterChange={(parameter, value) => setSearch(writeParameters(search, document.parameters, { ...parameters, [parameter]: value }), { replace: true })}
            onSelect={(selection) => setShown(selection.layout.name)}
          />
        </div>
      </div>
    </>
  )
}

function Opened({ kind, name }: { kind: ViewKind; name: string }) {
  const area = AREA[kind]
  const opened = useQuery({ queryKey: ['views', 'open', kind, name], queryFn: () => viewsClient().open(kind, name), retry: false })
  if (opened.isPending) return <div className="empty" data-testid="views-loading" role="status">Opening {name}…</div>
  if (opened.isError) {
    return (
      <div className="empty" data-testid="views-error" role="alert">
        {opened.error instanceof Error ? opened.error.message : String(opened.error)} <Link to={area.path}>All {area.plural.toLowerCase()}</Link>
      </div>
    )
  }
  const { document, diagnostics } = opened.data
  if (!document) {
    return (
      <div className="empty" data-testid="views-refused" role="alert">
        <strong>The {area.singular} {name} is not shown: it has errors.</strong>
        <Problems diagnostics={diagnostics} />
        <Link to={area.path}>All {area.plural.toLowerCase()}</Link>
      </div>
    )
  }
  if (kind === 'report') {
    return (
      <div className="empty" data-testid="views-report-note" role="note">
        <strong data-testid="views-title">{document.title}</strong>
        <p>Reports are not drawn yet: a report is a paged flow layout, and its screen view (page-break guides, print preview) is the next slice (DBR14). This one is valid and has the layouts {document.layouts.map((l) => `${l.name} (${l.type})`).join(', ')}.</p>
        <Link to={area.path} data-testid="views-back">All reports</Link>
      </div>
    )
  }
  return <Board key={name} kind={kind} name={name} document={document} />
}

/**
 * The Dashboards and Reports tabs of the Views page (plan 32 DBR23, M1): the documents of the fixture source (and any file opened), and one document drawn with `DashboardView` from the shared visuals:
 * the layout the window selects or the one picked (`?layout=`), parameters kept in the address (`?p.region=West`) so a link opens the same board, only the widgets that use a changed parameter resolved again.
 * Flow layouts and reports are not drawn yet (DBR14); canvas layouts are drawn (DBR07); there is no server route: the source is recorded results until the `views` module (DBR11) answers.
 */
export function DashboardsPanel({ kind }: { kind: ViewKind }) {
  const { name } = useParams()
  return name ? <Opened kind={kind} name={decodeURIComponent(name)} /> : <Listing kind={kind} />
}
