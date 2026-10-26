import { useQuery } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { proxyApi } from '../api'
import { ProxyShell } from '../ProxyShell'

function Figure({ label, value, testId, warn }: { label: string; value: string | number; testId: string; warn?: boolean }) {
  return (
    <div className="card" style={{ minWidth: 150 }} data-testid={testId} data-warn={warn ? 'true' : undefined}>
      <div className="card-body"><div className="hint">{label}</div><div style={{ font: '600 22px var(--ui)' }}>{value}</div></div>
    </div>
  )
}

function duration(seconds: number) {
  if (seconds < 90) return `${seconds} s`
  if (seconds < 5400) return `${Math.round(seconds / 60)} min`
  return seconds < 172800 ? `${Math.round(seconds / 3600)} h` : `${Math.round(seconds / 86400)} days`
}

/** What this proxy is, how long it has been up, how busy and how troubled it is, and what it last told the feed. */
export function OverviewPage() {
  const isAdmin = useIsAdmin()
  const overview = useQuery({ queryKey: ['proxy', 'overview'], queryFn: proxyApi.overview, refetchInterval: 5000 })
  const feed = useQuery({ queryKey: ['proxy', 'notifications'], queryFn: proxyApi.activity.notifications, refetchInterval: 10_000 })
  const o = overview.data
  return (
    <ProxyShell crumbs={[{ label: 'Overview', heading: true }]}>
      <div className="pane" data-testid="proxy-overview">
        <div className="page-head">
          <h1 className="page-title">DbDataProxy</h1>
          <span className="page-note" data-testid="proxy-version">{o ? `Version ${o.version}, up ${duration(o.uptime_seconds)}, serving Flight SQL on ${o.listen}.` : ''}</span>
        </div>
        <ErrorBanner error={overview.error} />
        {o && (
          <div className="row" style={{ gap: 12, flexWrap: 'wrap', marginBottom: 18 }}>
            <Figure label="Data sources" value={o.data_sources} testId="figure-data-sources" />
            <Figure label="Users" value={o.users + o.windows_accounts} testId="figure-users" />
            <Figure label="Signed in" value={o.sessions} testId="figure-sessions" />
            <Figure label="Running queries" value={o.running_queries} testId="figure-queries" />
            <Figure label="Failures, last hour" value={o.failures_last_hour} testId="figure-failures" warn={o.failures_last_hour > 0} />
            <Figure label="Locked-out clients" value={o.locked_out_clients} testId="figure-lockouts" warn={o.locked_out_clients > 0} />
          </div>
        )}
        <div className="card flush" data-testid="proxy-feed">
          <div className="card-head"><span className="card-title">What it last told you</span></div>
          {feed.data?.length === 0 && <div className="empty" data-testid="proxy-feed-empty">Nothing to report.</div>}
          {[...(feed.data ?? [])].reverse().map((n) => (
            <div key={n.id} className="grid-row" style={{ gridTemplateColumns: '1.2fr 4fr 1.4fr', gap: 14 }} data-testid="proxy-feed-item">
              <span className="mono">{n.kind}</span><span>{n.message}</span><span className="hint">{new Date(n.created).toLocaleString()}</span>
            </div>
          ))}
        </div>
        {!isAdmin && <p className="hint" style={{ marginTop: 12 }}>The other screens are for administrators.</p>}
      </div>
    </ProxyShell>
  )
}
