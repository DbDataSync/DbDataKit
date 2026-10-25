import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { proxyApi } from '../api'
import { ProxyShell } from '../ProxyShell'

const when = (iso: string) => new Date(iso).toLocaleString()

/** What is happening now (sessions, running queries with a cancel, the limits and who is locked out) and what went wrong lately. Refreshes itself. */
export function ActivityPage() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const poll = { refetchInterval: 5000, enabled: isAdmin }
  const sessions = useQuery({ queryKey: ['proxy', 'sessions'], queryFn: proxyApi.activity.sessions, ...poll })
  const queries = useQuery({ queryKey: ['proxy', 'queries'], queryFn: proxyApi.activity.queries, ...poll })
  const limits = useQuery({ queryKey: ['proxy', 'limits'], queryFn: proxyApi.activity.limits, ...poll })
  const failures = useQuery({ queryKey: ['proxy', 'failures'], queryFn: proxyApi.activity.failures, ...poll })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['proxy'] })
  const cancel = useMutation({ mutationFn: proxyApi.activity.cancelQuery, onSuccess: refresh })
  const endSession = useMutation({ mutationFn: proxyApi.activity.endSession, onSuccess: refresh })
  const lift = useMutation({ mutationFn: (h: { client: string; user: string }) => proxyApi.activity.liftLockout(h.client, h.user), onSuccess: refresh })

  if (!isAdmin) return <ProxyShell crumbs={[{ label: 'Activity', heading: true }]}><div className="pane"><div className="empty">This screen is for administrators.</div></div></ProxyShell>
  const held = limits.data?.login_throttle.held ?? []
  return (
    <ProxyShell crumbs={[{ label: 'Activity', heading: true }]}>
      <div className="pane" data-testid="proxy-activity">
        <div className="page-head"><h1 className="page-title">Activity</h1><span className="page-note">Refreshes every few seconds.</span></div>
        <ErrorBanner error={sessions.error ?? queries.error ?? limits.error ?? failures.error ?? cancel.error ?? endSession.error ?? lift.error} />

        <h2 className="card-title">Running queries</h2>
        <div className="card flush" data-testid="queries-table">
          {(queries.data ?? []).length === 0 && <div className="empty">Nothing is running.</div>}
          {(queries.data ?? []).map((q) => (
            <div key={q.id} className="grid-row" style={{ gridTemplateColumns: '1fr 1fr 4fr 1fr', gap: 14 }} data-testid={`query-${q.id}`}>
              <span className="mono">{q.user}</span><span className="mono">{q.data_source}</span><span className="mono">{q.sql ?? q.kind} · {q.running_seconds} s</span>
              <button type="button" className="btn" onClick={() => cancel.mutate(q.id)} data-testid="query-cancel">Cancel</button>
            </div>
          ))}
        </div>

        <h2 className="card-title" style={{ marginTop: 24 }}>Sessions</h2>
        <div className="card flush" data-testid="sessions-table">
          {(sessions.data ?? []).length === 0 && <div className="empty">Nobody is signed in.</div>}
          {(sessions.data ?? []).map((s) => (
            <div key={s.id} className="grid-row" style={{ gridTemplateColumns: '1fr 1fr 2fr 2fr 1fr', gap: 14 }} data-testid={`session-${s.id}`}>
              <span className="mono">{s.user}</span><span>{s.method}</span><span>last used {when(s.last_used)}</span><span>expires {when(s.expires)}</span>
              <button type="button" className="btn" onClick={() => endSession.mutate(s.id)} data-testid="session-end">End</button>
            </div>
          ))}
        </div>

        <h2 className="card-title" style={{ marginTop: 24 }}>Limits</h2>
        <div className="card flush" data-testid="limits-table">
          {(limits.data?.data_sources ?? []).map((l) => (
            <div key={l.data_source} className="grid-row" style={{ gridTemplateColumns: '2fr 1fr 1fr 1fr 1fr', gap: 14 }} data-testid={`limit-${l.data_source}`}>
              <span className="mono">{l.data_source}</span><span>{l.limited ? `max ${l.max_concurrent_queries}` : 'no limit'}</span><span>{l.active} running</span><span>{l.waiting} waiting</span><span>{l.refused_total} refused</span>
            </div>
          ))}
        </div>

        <h2 className="card-title" style={{ marginTop: 24 }}>Locked out</h2>
        <div className="card flush" data-testid="lockouts-table">
          {held.length === 0 && <div className="empty">Nobody is locked out.</div>}
          {held.map((h) => (
            <div key={`${h.client}|${h.user}`} className="grid-row" style={{ gridTemplateColumns: '2fr 2fr 1fr 1fr', gap: 14 }}>
              <span className="mono">{h.user}</span><span className="mono">{h.client}</span><span>{h.failures} failures</span>
              <button type="button" className="btn" onClick={() => lift.mutate(h)} data-testid="lockout-lift">Lift</button>
            </div>
          ))}
        </div>

        <h2 className="card-title" style={{ marginTop: 24 }}>Recent failures</h2>
        <div className="card flush" data-testid="failures-table">
          {(failures.data ?? []).length === 0 && <div className="empty">No failure lately.</div>}
          {(failures.data ?? []).map((f, i) => (
            <div key={i} className="grid-row" style={{ gridTemplateColumns: '2fr 1fr 1fr 5fr', gap: 14 }}>
              <span>{when(f.at)}</span><span className="mono">{f.data_source}</span><span>{f.kind}</span><span>{f.message}</span>
            </div>
          ))}
        </div>
      </div>
    </ProxyShell>
  )
}
