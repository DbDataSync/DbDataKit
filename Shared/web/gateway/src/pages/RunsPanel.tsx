import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import type { ReplayResult, RunRow } from '../runTypes'

const when = (iso: string) => new Date(iso).toLocaleString()

/** The explorer's link that opens a record's statement. */
export const openInExplorer = (r: RunRow) => `/models/${encodeURIComponent(r.model)}/explore?run=${r.id}`

/** One line of a run, for a list and for a test: the statement shortened, how it ended, and the model version it met. */
export function runLine(r: RunRow): string {
  const text = r.text.replace(/\s+/g, ' ').trim()
  return text.length > 90 ? text.slice(0, 89) + '…' : text
}

export interface RunsViewProps {
  runs: RunRow[] | undefined
  loading: boolean
  error: unknown
  retentionDays?: number
  all: boolean
  onAll: (all: boolean) => void
  replaying: number | null
  replayed: ReplayResult | undefined
  replayError: string | null
  onReplay: (id: number) => void
}

/**
 * The record of recent runs (plan 14a SL24): the person's own, newest first, and for an administrator everyone's; each row has the statement, how it ended, the request hash and the model version, and **Open in explorer** and **Replay**
 * (the same request on the version the record names; a version that is gone is said so). A log, not authored: nothing here edits a record.
 */
export function RunsView(p: RunsViewProps) {
  return (
    <section data-testid="runs" style={{ marginTop: 24 }}>
      <h2 className="card-title">Recent runs</h2>
      <div className="hint">Kept {p.retentionDays ?? 30} days; the rows are never kept. <label><input type="checkbox" data-testid="runs-all" checked={p.all} onChange={(e) => p.onAll(e.target.checked)} /> Everyone's (administrators)</label></div>
      <ErrorBanner error={p.error} />
      {p.loading && <div className="empty">Loading…</div>}
      {p.runs && p.runs.length === 0 && <div className="empty" data-testid="runs-none">Nothing has been run yet.</div>}
      {p.replayError && <div role="alert" className="hint" data-testid="replay-error">{p.replayError}</div>}
      {p.replayed && (
        <div className="hint" role="status" data-testid="replay-done">
          Replayed run {p.replayed.replay_of} on version {p.replayed.model_version ?? '-'}: {p.replayed.rows.length} row{p.replayed.rows.length === 1 ? '' : 's'}, {p.replayed.same_hash ? 'the same request hash' : 'a different request hash'}.
        </div>
      )}
      {p.runs && p.runs.length > 0 && (
        <div className="card flush">
          <div className="grid-head" style={{ gridTemplateColumns: '2.6fr 0.8fr 1fr 1.1fr 1.4fr 1.3fr', gap: 12 }}><span>Statement</span><span>Status</span><span>Hash</span><span>Version · time</span><span>Who</span><span /></div>
          {p.runs.map((r) => (
            <div key={r.id} className="grid-row auto" style={{ gridTemplateColumns: '2.6fr 0.8fr 1fr 1.1fr 1.4fr 1.3fr', gap: 12 }} data-testid={`run-${r.id}`}>
              <span className="mono" style={{ overflowWrap: 'anywhere' }} data-testid={`run-text-${r.id}`}>{runLine(r)}{r.replay_of != null && <div className="hint">replay of {r.replay_of}</div>}</span>
              <span data-testid={`run-status-${r.id}`}>{r.kind === 'explain' ? 'explain' : r.status}{r.error_code && <div className="hint mono">{r.error_code}</div>}</span>
              <span className="mono" data-testid={`run-hash-${r.id}`}>{r.request_hash ? r.request_hash.slice(0, 10) : '-'}</span>
              <span>{r.model_version ?? '-'} · {r.duration_ms} ms<div className="hint">{when(r.started_at)}</div></span>
              <span className="mono" style={{ overflowWrap: 'anywhere' }} data-testid={`run-caller-${r.id}`}>{r.caller}</span>
              <span className="row" style={{ gap: 6 }}>
                <Link className="btn btn-sm" to={openInExplorer(r)} data-testid={`run-open-${r.id}`}>Open in explorer</Link>
                <button type="button" className="btn btn-sm" data-testid={`run-replay-${r.id}`} disabled={p.replaying === r.id || r.kind === 'refused'} onClick={() => p.onReplay(r.id)}>Replay</button>
              </span>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}

/** The recent runs of the person on this Gateway (all models, or one). */
export function RunsPanel({ model }: { model?: string }) {
  const [all, setAll] = useState(false)
  const list = useQuery({ queryKey: ['gateway', 'runs', model ?? '', all], queryFn: () => gatewayApi.runs.list({ model, all, limit: 50 }), refetchOnMount: 'always', retry: false })
  const replay = useMutation({ mutationFn: (id: number) => gatewayApi.runs.replay(id), onSuccess: () => void list.refetch() })
  const failed = replay.error instanceof ApiError ? replay.error.message : replay.error instanceof Error ? replay.error.message : null
  return (
    <RunsView runs={list.data?.runs} loading={list.isLoading} error={list.error} retentionDays={list.data?.retention_days} all={all} onAll={setAll}
      replaying={replay.isPending ? (replay.variables ?? null) : null} replayed={replay.data} replayError={failed} onReplay={(id) => replay.mutate(id)} />
  )
}
