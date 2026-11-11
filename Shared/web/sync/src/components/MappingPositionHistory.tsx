import { useState } from 'react'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { useMappingPositionHistory, useRewindMappingPosition } from '../api/hooks'
import type { PositionChange } from '../api/types'

export interface PositionHistoryViewProps {
  changes: PositionChange[] | undefined
  loading: boolean
  error: unknown
  /** Only an administrator rewinds (the server refuses anyone else). */
  canRewind: boolean
  busy: boolean
  /** The change a rewind was asked for and is waiting for its confirmation, or null. */
  confirming: number | null
  /** The position the mapping holds after a rewind that just went through, or null. */
  rewoundTo: string | null
  onConfirm: (id: number | null) => void
  onRewind: (id: number) => void
}

const shown = (value: string | null) => (value === null ? 'none' : value.length > 40 ? `${value.slice(0, 37)}...` : value)

/**
 * A mapping's position history (plan 28c E15b): what the progress store recorded for its position, intent and hold, newest first, each with who made it, and "rewind to before this" for an administrator
 * (a second, explicit step: the next pass re-reads everything since that position and writes it again, which is correct and can be slow on a big table). A pure view, so it is tested without a server.
 */
export function PositionHistoryView(p: PositionHistoryViewProps) {
  if (p.loading) return <span className="hint" data-testid="position-history-loading">Reading the history…</span>
  if (p.error != null) return <ErrorBanner error={p.error} />
  const changes = p.changes ?? []
  if (changes.length === 0)
    return <span className="hint" data-testid="position-history-none">This mapping has no recorded position changes yet (the history is kept 30 days).</span>
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      {p.rewoundTo !== null && (
        <div className="banner ok" role="status" data-testid="position-history-rewound">
          Rewound. The mapping now holds position <span className="mono">{shown(p.rewoundTo)}</span>; its next pass reads from there again.
        </div>
      )}
      <table className="table" data-testid="position-history">
        <thead>
          <tr><th>When</th><th>What</th><th>By</th><th /></tr>
        </thead>
        <tbody>
          {changes.map((c) => (
            <tr key={c.id} data-testid="position-change">
              <td>{new Date(c.at).toLocaleString()}</td>
              <td className="mono">{c.part}: {shown(c.old)} → {shown(c.new)}</td>
              <td>{c.actor ?? 'a run'}</td>
              <td>
                {p.canRewind && p.confirming !== c.id && (
                  <button type="button" className="btn-link" disabled={p.busy} onClick={() => p.onConfirm(c.id)} data-testid="position-rewind">
                    Rewind to before this
                  </button>
                )}
                {p.canRewind && p.confirming === c.id && (
                  <span className="row" style={{ gap: 6 }}>
                    <button type="button" className="btn btn-danger btn-sm" disabled={p.busy} onClick={() => p.onRewind(c.id)} data-testid="position-rewind-confirm">
                      {p.busy ? 'Rewinding…' : 'Rewind and re-read'}
                    </button>
                    <button type="button" className="btn btn-sm" onClick={() => p.onConfirm(null)} data-testid="position-rewind-cancel">Cancel</button>
                  </span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {!p.canRewind && <span className="hint">Rewinding a position needs the admin role.</span>}
    </div>
  )
}

/** The history of one mapping's position inside the mapping's dialog, read when it is opened. */
export function MappingPositionHistory({ replicationName, mappingName }: { replicationName: string; mappingName: string }) {
  const isAdmin = useIsAdmin()
  const [open, setOpen] = useState(false)
  const [confirming, setConfirming] = useState<number | null>(null)
  const [rewoundTo, setRewoundTo] = useState<string | null>(null)
  const history = useMappingPositionHistory(replicationName, mappingName, open)
  const rewind = useRewindMappingPosition(replicationName, mappingName)
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <button type="button" className="btn-link" onClick={() => setOpen(!open)} aria-expanded={open} data-testid="position-history-toggle">
        {open ? 'Hide position history' : 'Position history…'}
      </button>
      {open && (
        <PositionHistoryView
          changes={history.data}
          loading={history.isLoading}
          error={history.error ?? rewind.error}
          canRewind={isAdmin}
          busy={rewind.isPending}
          confirming={confirming}
          rewoundTo={rewoundTo}
          onConfirm={setConfirming}
          onRewind={(id) => rewind.mutate(id, { onSuccess: (state) => { setConfirming(null); setRewoundTo(state.watermark ?? '') } })}
        />
      )}
    </div>
  )
}
