import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { ErrorBanner, Link, useIsAdmin } from '@dbdatakit/shell'
import { proxyApi } from '../api'
import { ProxyShell } from '../ProxyShell'

const crumbs = [{ label: 'Configuration history', heading: true }]

/** Every change to the configuration, who made it and when. Secrets are masked in what a change shows. */
export function ConfigHistoryPage() {
  const isAdmin = useIsAdmin()
  const history = useQuery({ queryKey: ['proxy', 'history'], queryFn: proxyApi.history.list, enabled: isAdmin })
  if (!isAdmin) return <ProxyShell crumbs={crumbs}><div className="pane"><div className="empty">This screen is for administrators.</div></div></ProxyShell>
  return (
    <ProxyShell crumbs={crumbs}>
      <div className="pane" data-testid="proxy-config-history">
        <div className="page-head"><h1 className="page-title">Configuration history</h1><span className="page-note">Each save is a commit by the person who made it.</span></div>
        <ErrorBanner error={history.error} />
        <div className="card flush" data-testid="commits-table">
          {(history.data ?? []).length === 0 && !history.isLoading && <div className="empty">No change recorded yet.</div>}
          {(history.data ?? []).map((c) => (
            <div key={c.sha} className="grid-row" style={{ gridTemplateColumns: '1fr 4fr 2fr 2fr', gap: 14 }} data-testid={`commit-${c.sha}`}>
              <Link to={`/config/${c.sha}`} className="mono">{c.sha.slice(0, 8)}</Link><span>{c.message}</span><span>{c.author}</span><span>{new Date(c.when).toLocaleString()}</span>
            </div>
          ))}
        </div>
      </div>
    </ProxyShell>
  )
}

/** What one change did to each file. */
export function ConfigCommitPage() {
  const { sha = '' } = useParams()
  const isAdmin = useIsAdmin()
  const commit = useQuery({ queryKey: ['proxy', 'history', sha], queryFn: () => proxyApi.history.commit(sha), enabled: isAdmin })
  const c = commit.data
  return (
    <ProxyShell crumbs={[{ label: 'Configuration history', to: '/config' }, { label: sha.slice(0, 8), heading: true }]}>
      <div className="pane" data-testid="proxy-config-commit">
        <ErrorBanner error={commit.error} />
        {c && <div className="page-head"><h1 className="page-title">{c.message}</h1></div>}
        {c?.truncated && <div className="hint">This change is large; only part of it is shown.</div>}
        {(c?.changes ?? []).map((f) => (
          <div className="card" key={f.path} style={{ marginBottom: 16 }} data-testid={`change-${f.path}`}>
            <div className="card-head"><span className="card-title mono">{f.path}</span><span className="hint">{f.kind}</span></div>
            <div className="card-body" style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16 }}>
              <pre className="mono" data-testid="change-before">{f.before ?? ''}</pre><pre className="mono" data-testid="change-after">{f.after ?? ''}</pre>
            </div>
          </div>
        ))}
      </div>
    </ProxyShell>
  )
}
