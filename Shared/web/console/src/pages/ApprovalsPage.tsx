import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ErrorBanner } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { loadApprovals, waited, type ApprovalRow } from '../approvals'

const kindText: Record<ApprovalRow['kind'], string> = { 'change-request': 'Change request', 'app-trust': 'App to approve' }

/**
 * What waits for a decision on the servers of the suite, in one list: the change requests of the Store's models (an agent's write, or a model that asks for approval) and the app versions of a Gateway that ask to be trusted and
 * have no approval for their exact files. The longest wait is first, and an agent's write comes before a person's. A row says whether you may decide it and opens the screen where it is read and decided; nothing is decided here,
 * since the proposed rows and the files are on those screens. Asked of each server through the relay every thirty seconds; a server that does not answer is named above the list, and the others still show.
 */
export function ApprovalsPage() {
  const instances = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list })
  const servers = instances.data ?? []
  const view = useQuery({
    queryKey: ['approvals', servers.map((s) => `${s.id}:${s.reachable}`).join(',')],
    queryFn: () => loadApprovals(servers),
    enabled: instances.isSuccess,
    refetchInterval: 30_000,
  })
  const rows = view.data?.rows ?? []
  const mine = rows.filter((r) => r.canDecide).length

  return (
    <ConsoleShell crumbs={[{ label: 'Approvals', heading: true }]}>
      <div className="pane" data-testid="approvals-page">
        <div className="page-head">
          <h1 className="page-title">Approvals</h1>
          <span className="page-note">What waits for a decision on your Store and Gateway servers.</span>
        </div>
        <ErrorBanner error={instances.error ?? view.error} />
        {(view.data?.problems ?? []).map((p) => (
          <div key={p.server} className="empty" data-testid="approvals-problem">{`${p.server} did not answer: ${p.message}`}</div>
        ))}
        {view.isLoading && <div className="hint" data-testid="approvals-loading">Asking the servers…</div>}
        {view.isSuccess && rows.length === 0 && <div className="empty" data-testid="approvals-none">Nothing waits for approval on the servers that answered.</div>}
        {rows.length > 0 && (
          <>
            <p className="hint" data-testid="approvals-summary">{rows.length} waiting, {mine} that you may decide.</p>
            <div className="card flush" data-testid="approvals-rows" role="table" aria-label="Waiting for approval">
              <div className="grid-head" role="row" style={{ gridTemplateColumns: '1.2fr 1fr 1.8fr 2.6fr 1.2fr 0.9fr 1fr', gap: 14 }}><span>Kind</span><span>Server</span><span>What</span><span>Detail</span><span>From</span><span>Waiting</span><span /></div>
              {rows.map((row) => (
                <div key={row.key} role="row" className="grid-row" style={{ gridTemplateColumns: '1.2fr 1fr 1.8fr 2.6fr 1.2fr 0.9fr 1fr', gap: 14 }} data-testid="approvals-row" data-kind={row.kind} data-can-decide={row.canDecide}>
                  <span data-testid="approvals-kind">{kindText[row.kind]}{row.byAgent ? ' (an agent)' : ''}</span>
                  <span>{row.serverName}</span>
                  <span className="mono">{row.subject}</span>
                  <span>{row.detail}</span>
                  <span>{row.by ?? ''}</span>
                  <span data-testid="approvals-waited">{waited(row.since)}</span>
                  <span className="row" style={{ gap: 8 }}>
                    <Link className="btn" to={row.link} data-testid="approvals-open">{row.canDecide ? 'Review' : 'Read'}</Link>
                    {!row.canDecide && <span className="hint" data-testid="approvals-cannot">You may not decide it</span>}
                  </span>
                </div>
              ))}
            </div>
          </>
        )}
      </div>
    </ConsoleShell>
  )
}
