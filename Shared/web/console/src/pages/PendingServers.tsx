import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { consoleApi, instancesKey, pendingKey, type PendingServer } from '../api'

/** How often the list is asked for again: servers announce every half minute, so a new one shows within a few seconds of the console hearing it. */
export const PENDING_REFRESH_MS = 5_000

const COLUMNS = '1.4fr 1.4fr 2fr 1.4fr 1.6fr'

/** What the person reads when a call on a waiting server failed: the console's own words, or a plain sentence for what it did not say. Never a stack or a status line. */
export function failureText(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 403 || error.status === 401) return 'Only administrators can confirm or reject servers here.'
    if (error.status === 404) return 'That server is no longer waiting. It may have been confirmed or rejected already.'
    return error.message
  }
  return 'The console did not answer. Try again.'
}

/** "12 s ago", "3 min ago": how long since the server last announced itself, so a server that has gone quiet reads as one. */
export function since(iso: string, now: number): string {
  const seconds = Math.max(0, Math.round((now - Date.parse(iso)) / 1000))
  if (Number.isNaN(seconds)) return 'unknown'
  if (seconds < 90) return `${seconds} s ago`
  const minutes = Math.round(seconds / 60)
  return minutes < 90 ? `${minutes} min ago` : `${Math.round(minutes / 60)} h ago`
}

export interface PendingServersViewProps {
  /** Only administrators see the list and its buttons; for anybody else the view is nothing at all. */
  canManage: boolean
  servers: PendingServer[] | undefined
  loading: boolean
  /** Why the list could not be read, if it could not. */
  listError: unknown
  /** The id of the server a call is under way for, so its buttons wait. */
  busy: string | null
  /** What went wrong for a server's last confirm or reject, by id. */
  failures: Record<string, string>
  now: number
  onConfirm: (id: string) => void
  onReject: (id: string) => void
}

/** One waiting server: what it calls itself, where it says it is, when it last announced, and the two things an administrator may do about it. Its code is not here and never was. */
function PendingRow({ server, busy, failure, now, onConfirm, onReject }: {
  server: PendingServer
  busy: boolean
  failure: string | undefined
  now: number
  onConfirm: (id: string) => void
  onReject: (id: string) => void
}) {
  return (
    <div data-testid={`pending-row-${server.name}`}>
      <div className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
        <span>{server.name}</span>
        <span className="mono">{server.product}</span>
        <span className="mono" data-testid="pending-url">{server.url}</span>
        <span data-testid="pending-last-seen" title={server.lastSeen}>{since(server.lastSeen, now)}</span>
        <span className="row" style={{ gap: 8 }}>
          <button type="button" className="btn btn-primary" disabled={busy} onClick={() => onConfirm(server.id)} data-testid="pending-confirm-button">{busy ? 'Working…' : 'Confirm'}</button>
          <button type="button" className="btn" disabled={busy} onClick={() => onReject(server.id)} data-testid="pending-reject-button">Reject</button>
        </span>
      </div>
      {failure && <div className="banner error" role="alert" data-testid="pending-error"><span className="mark">!</span><span>{failure}</span></div>}
    </div>
  )
}

/**
 * The servers that announced themselves (plan 13a, L03) and wait for an administrator: nothing has been contacted and nothing is trusted until Confirm, which pairs with the certificate the server presents, as pairing by code
 * does. Reject forgets the announcement; a server that keeps announcing is listed again, which the text says. Pure: the container below owns the calls.
 */
export function PendingServersView({ canManage, servers, loading, listError, busy, failures, now, onConfirm, onReject }: PendingServersViewProps) {
  if (!canManage) return null
  return (
    <div className="card" style={{ marginBottom: 20 }} data-testid="pending-card">
      <div className="card-head"><span className="card-title">Waiting for confirmation</span></div>
      <div className="card-body">
        <span className="hint">Servers that announced themselves to this console. Confirming one pairs with it and pins the certificate it presents: confirm only a server you recognise by its name and address. A rejected server is listed again while it keeps announcing.</span>
        <ErrorBanner error={listError ? failureText(listError) : null} />
      </div>
      <div className="card flush" data-testid="pending-table">
        {loading && <div className="empty">Loading…</div>}
        {!loading && !listError && (servers ?? []).length === 0 && <div className="empty" data-testid="pending-empty">No server is waiting to be confirmed.</div>}
        {(servers ?? []).length > 0 && (
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Name</span><span>Product</span><span>Address</span><span>Last announced</span><span></span>
          </div>
        )}
        {(servers ?? []).map((server) => (
          <PendingRow key={server.id} server={server} busy={busy === server.id} failure={failures[server.id]} now={now} onConfirm={onConfirm} onReject={onReject} />
        ))}
      </div>
    </div>
  )
}

/** The list on the Servers page: asked for again every few seconds, and what a confirmation pairs shows in the servers' list at once. */
export function PendingServers() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const pending = useQuery({ queryKey: pendingKey, queryFn: consoleApi.instances.pending, enabled: isAdmin, refetchInterval: PENDING_REFRESH_MS })
  const [busy, setBusy] = useState<string | null>(null)
  const [failures, setFailures] = useState<Record<string, string>>({})

  const settle = (id: string, call: (id: string) => Promise<unknown>) => {
    setBusy(id)
    setFailures(({ [id]: _forgotten, ...rest }) => rest)
    call(id)
      .catch((error: unknown) => setFailures((all) => ({ ...all, [id]: failureText(error) })))
      // Whatever happened, what the console now knows is asked for again: a failed confirm may have been a server that stopped waiting.
      .finally(() => {
        setBusy(null)
        void queryClient.invalidateQueries({ queryKey: pendingKey })
        void queryClient.invalidateQueries({ queryKey: instancesKey })
      })
  }

  return (
    <PendingServersView
      canManage={isAdmin}
      servers={pending.data}
      loading={pending.isLoading}
      listError={pending.error}
      busy={busy}
      failures={failures}
      now={Date.now()}
      onConfirm={(id) => settle(id, consoleApi.instances.confirm)}
      onReject={(id) => settle(id, consoleApi.instances.reject)}
    />
  )
}
