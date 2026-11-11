import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ErrorBanner, shellApi, useIsAdmin } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { checklist, loadFacts, remaining, setupApi, type SetupRow } from '../setup'

const setupKey = ['setup'] as const

/** The checklist as data: the install's facts and the skipped ids, read together. Used by the page and the Home banner. */
export function useSetup(enabled: boolean) {
  const instances = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list, enabled })
  const users = useQuery({ queryKey: ['users'], queryFn: shellApi.users.list, enabled })
  const skipped = useQuery({ queryKey: [...setupKey, 'skipped'], queryFn: setupApi.items, enabled })
  const servers = instances.data ?? []
  const facts = useQuery({
    queryKey: [...setupKey, 'facts', servers.map((s) => `${s.id}:${s.reachable}`).join(','), (users.data ?? []).length],
    queryFn: () => loadFacts(servers, users.data ?? []),
    enabled: enabled && instances.isSuccess && users.isSuccess,
  })
  const ready = facts.isSuccess && skipped.isSuccess
  const rows = ready ? checklist(facts.data, new Set(skipped.data.items.filter((i) => i.skipped).map((i) => i.item))) : []
  return { rows, ready, error: instances.error ?? users.error ?? skipped.error ?? facts.error }
}

const STATUS_WORDS: Record<SetupRow['status'], string> = { done: 'Done', todo: 'To do', skipped: 'Skipped', unavailable: 'Not available yet' }

/**
 * The setup checklist (plan 23a FQ21): the first steps of a new install, each opening the page that does it. A step is **finished by what the install has** (a person with a passkey, a paired server, a connection, a Build project),
 * never by a click; it can be skipped, which takes it off the Home banner but not off this list, and put back. Administrators only.
 */
export function SetupPage() {
  const isAdmin = useIsAdmin()
  const client = useQueryClient()
  const setup = useSetup(isAdmin)
  const skip = useMutation({
    mutationFn: ({ item, skipped }: { item: string; skipped: boolean }) => setupApi.skip(item, skipped),
    onSuccess: () => client.invalidateQueries({ queryKey: [...setupKey, 'skipped'] }),
  })
  if (!isAdmin) {
    return (
      <ConsoleShell crumbs={[{ label: 'Setup', heading: true }]}>
        <div className="pane"><div className="empty">This screen is for administrators.</div></div>
      </ConsoleShell>
    )
  }
  const left = remaining(setup.rows).length
  return (
    <ConsoleShell crumbs={[{ label: 'Setup', heading: true }]}>
      <div className="pane" data-testid="setup-page">
        <div className="page-head">
          <h1 className="page-title">Setup</h1>
          <span className="page-note" data-testid="setup-summary">
            {setup.ready ? (left === 0 ? 'Nothing is left to do.' : `${left} step${left === 1 ? '' : 's'} left. A step is finished by what this install has, not by a click.`) : 'Reading what this install has…'}
          </span>
        </div>
        <ErrorBanner error={setup.error ?? skip.error} />
        <div className="card flush" data-testid="setup-list">
          {setup.rows.map((row) => (
            <div key={row.id} className="grid-row" style={{ gridTemplateColumns: '2.4fr 1fr 1.4fr', alignItems: 'start' }} data-testid={`setup-item-${row.id}`} data-status={row.status}>
              <span>
                <strong>{row.title}</strong>
                <div className="hint">{row.why}</div>
                {row.unavailable && <div className="hint" data-testid={`setup-unavailable-${row.id}`}>{row.unavailable}</div>}
              </span>
              <span data-testid={`setup-status-${row.id}`}>{STATUS_WORDS[row.status]}</span>
              <span className="row" style={{ gap: 8 }}>
                {row.status !== 'unavailable' && <Link className="btn btn-sm" to={row.to} data-testid={`setup-open-${row.id}`}>{row.status === 'done' ? 'Open' : 'Do it'}</Link>}
                {row.status === 'todo' && (
                  <button type="button" className="btn btn-sm" disabled={skip.isPending} onClick={() => skip.mutate({ item: row.id, skipped: true })} data-testid={`setup-skip-${row.id}`}>Skip</button>
                )}
                {row.status === 'skipped' && (
                  <button type="button" className="btn btn-sm" disabled={skip.isPending} onClick={() => skip.mutate({ item: row.id, skipped: false })} data-testid={`setup-unskip-${row.id}`}>Put back</button>
                )}
              </span>
            </div>
          ))}
        </div>
      </div>
    </ConsoleShell>
  )
}

/** On Home, for an administrator: how many steps are left, and the way to the list. Nothing when none is (skipped steps do not count). */
export function SetupBanner() {
  const isAdmin = useIsAdmin()
  const setup = useSetup(isAdmin)
  if (!isAdmin || !setup.ready) return null
  const left = remaining(setup.rows)
  if (left.length === 0) return null
  return (
    <div className="card" style={{ marginBottom: 14 }} data-testid="setup-banner" data-left={left.length}>
      <div className="card-body">
        <strong>Finish setting up:</strong> {left.length} step{left.length === 1 ? '' : 's'} left, next: {left[0].title}. <Link to="/setup" data-testid="setup-banner-open">Open the setup list</Link>
      </div>
    </div>
  )
}
