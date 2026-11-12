import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import { bytesText, eventsText, percent, scopeText, settingState, ttlText } from '../cache'
import type { CacheOverview } from '../cacheTypes'
import { GatewayShell } from '../GatewayShell'

export interface CacheViewProps {
  data: CacheOverview | undefined
  error: unknown
  isAdmin: boolean
  busy: boolean
  /** The dataset (or `*` for all) whose clear waits for a second click. */
  confirming: string | null
  cleared: string | null
  now?: Date
  onClear: (dataset: string | null) => void
}

const ALL = '*'

function Figure({ label, value, testId }: { label: string; value: string | number; testId: string }) {
  return (
    <div className="card" style={{ minWidth: 150 }} data-testid={testId}>
      <div className="card-body"><div className="hint">{label}</div><div style={{ font: '600 22px var(--ui)' }}>{value}</div></div>
    </div>
  )
}

/** The result cache of this instance (plan 24a UC16): the counts, the setting of each definition, what it heard of data changes, and a clear for an administrator. */
export function CacheView(p: CacheViewProps) {
  const d = p.data
  const clearButton = (dataset: string | null, testId: string) => {
    const key = dataset ?? ALL
    return (
      <button type="button" className="btn" disabled={p.busy} onClick={() => p.onClear(dataset)} data-testid={testId}>
        {p.confirming === key ? 'Click again to clear' : dataset === null ? 'Clear everything' : 'Clear'}
      </button>
    )
  }
  return (
    <>
      <div className="pane" data-testid="gateway-cache">
        <div className="page-head">
          <h1 className="page-title">Result cache</h1>
          <span className="page-note">{d ? `Instance ${d.instance}: up to ${bytesText(d.max_bytes)} in memory, ${bytesText(d.max_entry_bytes)} an entry. Counts are of this instance.` : ''}</span>
        </div>
        <ErrorBanner error={p.error} />
        {p.cleared && <div className="banner" data-testid="cache-cleared">{p.cleared}</div>}
        {d && (
          <>
            {!d.enabled && <p className="hint" data-testid="cache-off">The cache is off: no data source or model says <span className="mono">cache: enabled: true</span>. Nothing is stored.</p>}
            <div className="row" style={{ gap: 12, flexWrap: 'wrap', marginBottom: 18 }}>
              <Figure label="Hit rate" value={percent(d.stats.hit_rate)} testId="cache-hit-rate" />
              <Figure label="Hits" value={d.stats.hits} testId="cache-hits" />
              <Figure label="Misses" value={d.stats.misses} testId="cache-misses" />
              <Figure label="Entries" value={d.stats.entries} testId="cache-entries" />
              <Figure label="Size" value={bytesText(d.stats.bytes)} testId="cache-bytes" />
              <Figure label="Evictions" value={d.stats.evictions} testId="cache-evictions" />
            </div>

            <h2 className="card-title">Settings of the definitions</h2>
            <div className="card flush" data-testid="cache-settings">
              {d.settings.length === 0 && <div className="empty">This Gateway serves no data source or model.</div>}
              {d.settings.map((s) => {
                const state = settingState(s)
                return (
                  <div key={s.name} className="grid-row" style={{ gridTemplateColumns: '2fr 1fr 3fr 2fr 1fr', gap: 14 }} data-testid={`cache-setting-${s.name}`}>
                    <span className="mono">{s.name}</span>
                    <span data-testid={`cache-setting-state-${s.name}`} data-tone={state.tone}>{state.label}</span>
                    <span className="hint">{s.enabled ? `lives ${ttlText(s.ttl_seconds)}, shared by ${scopeText(s.scope)}` : 'the definition has no cache section, or says no'}</span>
                    <span>{s.entries} {s.entries === 1 ? 'entry' : 'entries'}</span>
                    {p.isAdmin && s.enabled ? clearButton(s.datasets[0] ?? s.name, `cache-clear-${s.name}`) : <span />}
                  </div>
                )
              })}
            </div>

            <h2 className="card-title" style={{ marginTop: 24 }}>By dataset</h2>
            <div className="card flush" data-testid="cache-datasets">
              {d.datasets.length === 0 && <div className="empty" data-testid="cache-datasets-none">Nothing has been asked of the cache yet.</div>}
              {d.datasets.map((r) => (
                <div key={r.name} className="grid-row" style={{ gridTemplateColumns: '3fr 1fr 1fr 1fr 1fr 1fr', gap: 14 }} data-testid={`cache-dataset-${r.name}`}>
                  <span className="mono">{r.name}</span><span>{r.entries} entries</span><span>{bytesText(r.bytes)}</span><span>{r.hits} hits</span><span>{r.misses} misses</span><span>{percent(r.hit_rate)}</span>
                </div>
              ))}
            </div>

            <h2 className="card-title" style={{ marginTop: 24 }}>Data changes</h2>
            <div className="card" data-testid="cache-events">
              <div className="card-body">
                <p data-testid="cache-events-text">{eventsText(d.events, p.now)}</p>
                <p className="hint">{d.events.shared ? 'This Gateway is one of a group: a change that reaches any instance clears all of them within a few seconds.' : 'This Gateway shares nothing with other instances.'}</p>
                {d.events.recent.map((e, i) => <div key={i} className="hint" data-testid="cache-event-recent"><span className="mono">{e.dataset}</span> by {e.product}: {e.reason}</div>)}
              </div>
            </div>

            {p.isAdmin && <div style={{ marginTop: 18 }}>{clearButton(null, 'cache-clear-all')}</div>}
          </>
        )}
      </div>
    </>
  )
}

/** The route: reads every few seconds; a clear asks for a second click. */
export function CachePage() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const [confirming, setConfirming] = useState<string | null>(null)
  const [cleared, setCleared] = useState<string | null>(null)
  const cache = useQuery({ queryKey: ['gateway', 'cache'], queryFn: gatewayApi.cache.read, refetchInterval: 5000 })
  const clear = useMutation({
    mutationFn: (dataset: string | null) => gatewayApi.cache.clear(dataset === null ? { all: true } : { dataset }),
    onSuccess: (r) => { setConfirming(null); setCleared(`${r.cleared} ${r.cleared === 1 ? 'entry' : 'entries'} cleared.`); return queryClient.invalidateQueries({ queryKey: ['gateway', 'cache'] }) },
    onError: () => setConfirming(null),
  })
  const onClear = (dataset: string | null) => {
    const key = dataset ?? ALL
    if (confirming !== key) { setConfirming(key); setCleared(null); return }
    clear.mutate(dataset)
  }
  return <GatewayShell crumbs={[{ label: 'Result cache', heading: true }]}><CacheView data={cache.data} error={cache.error ?? clear.error} isAdmin={isAdmin} busy={clear.isPending} confirming={confirming} cleared={cleared} onClear={onClear} /></GatewayShell>
}
