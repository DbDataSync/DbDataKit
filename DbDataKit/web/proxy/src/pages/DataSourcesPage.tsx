import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ApiError, ErrorBanner, useIsAdmin, useNavigate } from '@dbdatakit/shell'
import { proxyApi } from '../api'
import { ProxyShell } from '../ProxyShell'
import type { AdoptAnswer, Diagnostic, ServerClient } from '../types'

const COLUMNS = '1.6fr 0.9fr 0.8fr 0.6fr 1.2fr 2.2fr'

const keys = { list: ['proxy', 'data-sources'] as const, one: (name: string) => ['proxy', 'data-sources', name] as const }

/** The skeleton of a new data source: the shared connection format with the proxy's own settings under `extensions.proxy`. */
const TEMPLATE = (name: string) => `schema: connection/1
name: ${name}
engine: sqlserver
connection_string: "Server=HOST;Database=DB"
auth: { mode: sql, user: USER, password_secret: "env:${name.toUpperCase().replace(/[^A-Z0-9]/g, '_')}_PASSWORD" }
extensions:
  proxy:
    classifier: strict
`

export interface HandOverViewProps {
  clients: ServerClient[]
  /** The id of the client that manages the data source, if one does. */
  managedBy: string | null | undefined
  chosen: string
  busy: boolean
  done: AdoptAnswer | null
  failure: unknown
  onChoose: (client: string) => void
  onAdopt: () => void
}

/** The client a data source belongs to, as a person knows it: its name when it is paired here, otherwise its id; "by hand" when no client does. */
export function ownerLabel(managedBy: string | null | undefined, clients: ServerClient[]): string {
  if (!managedBy) return 'by hand'
  return clients.find((c) => c.id === managedBy)?.name ?? managedBy
}

/** One data source's hand-over, without its calls (so a test can draw it and press the button): choose a paired server client and hand the source over. */
export function HandOverView(p: HandOverViewProps) {
  if (p.clients.length === 0) return <span className="hint" data-testid="hand-over-none">No server paired</span>
  return (
    <span className="row" style={{ gap: 6, flexWrap: 'wrap' }} data-testid="hand-over">
      <select className="input sm" style={{ width: 150, flex: 'none' }} value={p.chosen} onChange={(e) => p.onChoose(e.target.value)} data-testid="hand-over-client" aria-label="Server client">
        <option value="">Hand to…</option>
        {p.clients.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
      </select>
      <button type="button" className="btn sm" disabled={p.chosen === '' || p.busy} onClick={p.onAdopt} data-testid="hand-over-button">{p.busy ? 'Handing over…' : 'Hand over'}</button>
      {p.done && <span className="hint" data-testid="hand-over-done">{p.done.adopted ? `Handed to ${ownerLabel(p.done.managed_by, p.clients)}.` : 'Already theirs.'}</span>}
      {p.failure != null && <span className="hint" role="alert" data-testid="hand-over-failed">{p.failure instanceof ApiError ? p.failure.message : 'The proxy did not answer. Try again.'}</span>}
    </span>
  )
}

function HandOver({ name, managedBy, clients }: { name: string; managedBy: string | null | undefined; clients: ServerClient[] }) {
  const queryClient = useQueryClient()
  const [chosen, setChosen] = useState('')
  const adopt = useMutation({
    mutationFn: () => proxyApi.dataSources.adopt(name, chosen),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.list }),
  })
  return <HandOverView clients={clients} managedBy={managedBy} chosen={chosen} busy={adopt.isPending} done={adopt.data ?? null} failure={adopt.error} onChoose={(c) => { setChosen(c); adopt.reset() }} onAdopt={() => adopt.mutate()} />
}

export function DataSourcesPage() {
  const isAdmin = useIsAdmin()
  const navigate = useNavigate()
  const sources = useQuery({ queryKey: keys.list, queryFn: proxyApi.dataSources.list, enabled: isAdmin })
  const clients = useQuery({ queryKey: ['proxy', 'clients'], queryFn: proxyApi.clients, enabled: isAdmin })
  const [name, setName] = useState('')
  if (!isAdmin) return <ProxyShell crumbs={[{ label: 'Data sources', heading: true }]}><div className="pane"><div className="empty">This screen is for administrators.</div></div></ProxyShell>
  return (
    <ProxyShell crumbs={[{ label: 'Data sources', heading: true }]}>
      <div className="pane" data-testid="proxy-data-sources">
        <div className="page-head">
          <h1 className="page-title">Data sources</h1>
          <span className="page-note">The databases clients can query. Each is a file; a change applies at once.</span>
        </div>
        <ErrorBanner error={sources.error ?? clients.error} />
        <div className="card flush" data-testid="data-sources-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}><span>Name</span><span>Engine</span><span>Access</span><span>Valid</span><span>Managed by</span><span>Hand to a server</span></div>
          {sources.isLoading && <div className="empty">Loading…</div>}
          {sources.data?.length === 0 && <div className="empty" data-testid="data-sources-empty">No data source yet.</div>}
          {(sources.data ?? []).map((s) => (
            <div key={s.name} className="grid-row auto" style={{ gridTemplateColumns: COLUMNS, gap: 14 }} data-testid={`data-source-${s.name}`}>
              <Link to={`/data-sources/${encodeURIComponent(s.name)}`} className="mono" data-testid="data-source-link">{s.name}</Link>
              <span>{s.engine ?? '—'}</span><span>{s.access ?? '—'}</span><span>{s.valid ? 'yes' : 'no'}</span>
              <span data-testid="data-source-owner">{ownerLabel(s.managed_by, clients.data ?? [])}</span>
              <HandOver name={s.name} managedBy={s.managed_by} clients={clients.data ?? []} />
            </div>
          ))}
        </div>
        <div className="card" style={{ marginTop: 20 }}>
          <div className="card-head"><span className="card-title">New data source</span></div>
          <div className="card-body">
            <div className="row" style={{ gap: 10 }}>
              <input className="input" placeholder="name" value={name} onChange={(e) => setName(e.target.value)} data-testid="new-data-source-name" />
              <button type="button" className="btn btn-primary" disabled={!/^[A-Za-z][A-Za-z0-9_.-]{0,63}$/.test(name)} onClick={() => navigate(`/data-sources/${encodeURIComponent(name)}?new=1`)} data-testid="new-data-source-button">Start</button>
            </div>
          </div>
        </div>
      </div>
    </ProxyShell>
  )
}

function Diagnostics({ items }: { items: Diagnostic[] }) {
  if (items.length === 0) return null
  return (
    <div className="card flush" style={{ marginBottom: 12 }} data-testid="data-source-diagnostics">
      {items.map((d, i) => (
        <div key={i} className="grid-row" style={{ gridTemplateColumns: '0.8fr 0.7fr 4fr 1.4fr', gap: 14 }} data-testid="data-source-diagnostic">
          <span className="mono">{d.code}</span><span data-severity={d.severity}>{d.severity}</span>
          <span>{d.message}{d.fix ? ` ${d.fix}` : ''}</span><span className="mono">{d.file.split(/[\\/]/).pop()}{d.line > 0 ? `:${d.line}` : ''}</span>
        </div>
      ))}
    </div>
  )
}

/** One data source's file: edit, save (against the version read), test, remove. */
export function DataSourceEditPage({ name, isNew }: { name: string; isNew: boolean }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const file = useQuery({ queryKey: keys.one(name), queryFn: () => proxyApi.dataSources.read(name), enabled: !isNew })
  const [draft, setDraft] = useState<string | null>(null)
  const text = draft ?? file.data?.text ?? (isNew ? TEMPLATE(name) : '')
  const [diagnostics, setDiagnostics] = useState<Diagnostic[]>([])
  const [notice, setNotice] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: () => proxyApi.dataSources.write(name, text, isNew ? null : file.data?.version ?? null),
    onSuccess: async (result) => {
      setDiagnostics(result.diagnostics)
      setNotice(result.created ? 'Created and in use.' : 'Saved and in use.')
      setDraft(null)
      await queryClient.invalidateQueries({ queryKey: ['proxy'] })
      if (isNew) navigate(`/data-sources/${encodeURIComponent(name)}`)
    },
    onError: (error: unknown) => {
      // a refused write carries the reasons; they are shown against the file, and the banner says only that it was refused
      setNotice(null)
      setDiagnostics(((error as ApiError).body as { diagnostics?: Diagnostic[] } | undefined)?.diagnostics ?? [])
    },
  })
  const test = useMutation({ mutationFn: () => proxyApi.dataSources.test(name) })
  const remove = useMutation({
    mutationFn: () => proxyApi.dataSources.remove(name, file.data!.version),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['proxy'] }); navigate('/data-sources') },
  })

  return (
    <ProxyShell crumbs={[{ label: 'Data sources', to: '/data-sources' }, { label: name, mono: true, heading: true }]}>
      <div className="pane" data-testid="data-source-edit">
        <ErrorBanner error={file.error ?? save.error ?? remove.error ?? test.error} />
        <Diagnostics items={diagnostics} />
        {notice && <div className="hint" data-testid="data-source-notice" style={{ marginBottom: 8 }}>{notice}</div>}
        <textarea className="input mono" rows={22} style={{ width: '100%' }} spellCheck={false} value={text} onChange={(e) => { setDraft(e.target.value); setNotice(null) }} data-testid="data-source-text" />
        <div className="row" style={{ gap: 10, marginTop: 12 }}>
          <button type="button" className="btn btn-primary" disabled={save.isPending} onClick={() => save.mutate()} data-testid="data-source-save">{isNew ? 'Create' : 'Save'}</button>
          {!isNew && <button type="button" className="btn" disabled={test.isPending} onClick={() => test.mutate()} data-testid="data-source-test">Test the connection</button>}
          {!isNew && <button type="button" className="btn" disabled={remove.isPending || !file.data} onClick={() => remove.mutate()} data-testid="data-source-remove">Remove</button>}
        </div>
        {test.data && (
          <div className="hint" style={{ marginTop: 10 }} data-testid="data-source-test-result" data-ok={test.data.ok ? 'true' : 'false'}>
            {test.data.ok ? `Connected (${test.data.elapsed_ms} ms).` : `Could not connect: ${test.data.message ?? 'no answer'}`}
          </div>
        )}
      </div>
    </ProxyShell>
  )
}
