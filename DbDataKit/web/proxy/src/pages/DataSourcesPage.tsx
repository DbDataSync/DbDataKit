import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ApiError, ErrorBanner, useIsAdmin, useNavigate } from '@dbdatakit/shell'
import { proxyApi } from '../api'
import { ProxyShell } from '../ProxyShell'
import type { Diagnostic } from '../types'

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

export function DataSourcesPage() {
  const isAdmin = useIsAdmin()
  const navigate = useNavigate()
  const sources = useQuery({ queryKey: keys.list, queryFn: proxyApi.dataSources.list, enabled: isAdmin })
  const [name, setName] = useState('')
  if (!isAdmin) return <ProxyShell crumbs={[{ label: 'Data sources', heading: true }]}><div className="pane"><div className="empty">This screen is for administrators.</div></div></ProxyShell>
  return (
    <ProxyShell crumbs={[{ label: 'Data sources', heading: true }]}>
      <div className="pane" data-testid="proxy-data-sources">
        <div className="page-head">
          <h1 className="page-title">Data sources</h1>
          <span className="page-note">The databases clients can query. Each is a file; a change applies at once.</span>
        </div>
        <ErrorBanner error={sources.error} />
        <div className="card flush" data-testid="data-sources-table">
          <div className="grid-head" style={{ gridTemplateColumns: '2fr 1fr 1fr 1fr', gap: 14 }}><span>Name</span><span>Engine</span><span>Access</span><span>Valid</span></div>
          {sources.isLoading && <div className="empty">Loading…</div>}
          {sources.data?.length === 0 && <div className="empty" data-testid="data-sources-empty">No data source yet.</div>}
          {(sources.data ?? []).map((s) => (
            <div key={s.name} className="grid-row" style={{ gridTemplateColumns: '2fr 1fr 1fr 1fr', gap: 14 }} data-testid={`data-source-${s.name}`}>
              <Link to={`/data-sources/${encodeURIComponent(s.name)}`} className="mono" data-testid="data-source-link">{s.name}</Link>
              <span>{s.engine ?? '—'}</span><span>{s.access ?? '—'}</span><span>{s.valid ? 'yes' : 'no'}</span>
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
