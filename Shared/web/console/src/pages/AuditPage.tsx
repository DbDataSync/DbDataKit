import { useState } from 'react'
import { useInfiniteQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { ConsoleShell } from '../ConsoleShell'
import { appendPage, auditApi, groups, noFilter, outcomeLabel, what, who, type AuditEntry, type AuditFilter } from '../audit'

const columns = '1.5fr 1.4fr 3.4fr 0.8fr'
const when = (iso: string) => new Date(iso).toLocaleString()

export interface AuditViewProps {
  entries: AuditEntry[]
  filter: AuditFilter
  loading: boolean
  error: unknown
  hasMore: boolean
  loadingMore: boolean
  onFilter: (filter: AuditFilter) => void
  onMore: () => void
}

/** The page without its data fetching, so it can be rendered and pressed in a test. */
export function AuditView({ entries, filter, loading, error, hasMore, loadingMore, onFilter, onMore }: AuditViewProps) {
  const set = (patch: Partial<AuditFilter>) => onFilter({ ...filter, ...patch })
  const narrowed = JSON.stringify(filter) !== JSON.stringify(noFilter)
  return (
    <div className="pane" data-testid="audit-page">
      <div className="page-head">
        <h1 className="page-title">Audit</h1>
        <span className="page-note">What the console recorded: sign-ins, invitations, people, servers paired and removed, and every write made on a server through it. Kept until an administrator sets a limit; never a value or a password.</span>
      </div>
      <ErrorBanner error={error} />
      <div className="row" style={{ gap: 12, flexWrap: 'wrap', marginBottom: 12 }} data-testid="audit-filter">
        <label>What <select value={filter.verb} onChange={(e) => set({ verb: e.target.value })} data-testid="audit-verb">
          <option value="">Everything</option>
          {groups.map((g) => <option key={g.prefix} value={g.prefix}>{g.label}</option>)}
        </select></label>
        <label>Outcome <select value={filter.outcome} onChange={(e) => set({ outcome: e.target.value as AuditFilter['outcome'] })} data-testid="audit-outcome">
          <option value="">Any</option>
          <option value="ok">Done</option>
          <option value="refused">Refused</option>
          <option value="failed">Failed</option>
        </select></label>
        <label>From <input type="date" value={filter.from} onChange={(e) => set({ from: e.target.value })} data-testid="audit-from" /></label>
        <label>To <input type="date" value={filter.to} onChange={(e) => set({ to: e.target.value })} data-testid="audit-to" /></label>
        {filter.actor && <button type="button" className="btn" onClick={() => set({ actor: '' })} data-testid="audit-clear-actor">Show everyone</button>}
        {narrowed && <button type="button" className="btn" onClick={() => onFilter(noFilter)} data-testid="audit-clear">Clear the filter</button>}
      </div>
      {loading && <div className="hint" data-testid="audit-loading">Reading the audit…</div>}
      {!loading && entries.length === 0 && !error && (
        <div className="empty" data-testid="audit-none">{narrowed ? 'Nothing recorded matches the filter.' : 'Nothing has been recorded yet.'}</div>
      )}
      {entries.length > 0 && (
        <div className="card flush" data-testid="audit-rows" role="table" aria-label="Audit">
          <div className="grid-head" role="row" style={{ gridTemplateColumns: columns, gap: 14 }}><span>When</span><span>Who</span><span>What</span><span>Outcome</span></div>
          {entries.map((e) => (
            <div key={e.id} role="row" className="grid-row" style={{ gridTemplateColumns: columns, gap: 14 }} data-testid="audit-row" data-verb={e.verb} data-outcome={e.outcome}>
              <span title={e.at}>{when(e.at)}</span>
              <span>
                {e.actor === 'anonymous'
                  ? who(e)
                  : <button type="button" className="link" onClick={() => set({ actor: e.actor })} title="Show only this person" data-testid="audit-actor">{who(e)}</button>}
              </span>
              <span data-testid="audit-what">{what(e)}{e.detail?.status ? <span className="hint"> ({e.detail.status})</span> : null}</span>
              <span data-testid="audit-outcome-text">{outcomeLabel(e.outcome)}</span>
            </div>
          ))}
        </div>
      )}
      {hasMore && <button type="button" className="btn" disabled={loadingMore} onClick={onMore} data-testid="audit-more" style={{ marginTop: 10 }}>{loadingMore ? 'Reading…' : 'Older entries'}</button>}
    </div>
  )
}

/** The audit trail of the console (UI05; administrators): the newest entries first, narrowed by what, outcome, person and dates, with a button for older ones. */
export function AuditPage() {
  const [filter, setFilter] = useState<AuditFilter>(noFilter)
  const query = useInfiniteQuery({
    queryKey: ['audit', filter],
    queryFn: ({ pageParam }) => auditApi.list(filter, pageParam),
    initialPageParam: null as number | null,
    getNextPageParam: (last) => last.next,
  })
  const entries = (query.data?.pages ?? []).reduce<AuditEntry[]>((all, page) => appendPage(all, page.entries), [])
  return (
    <ConsoleShell crumbs={[{ label: 'Audit', heading: true }]}>
      <AuditView entries={entries} filter={filter} loading={query.isLoading} error={query.error} hasMore={query.hasNextPage} loadingMore={query.isFetchingNextPage}
        onFilter={setFilter} onMore={() => void query.fetchNextPage()} />
    </ConsoleShell>
  )
}
