import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ErrorBanner } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { decideRequest, decisionProblem, loadApprovals, waited, type ApprovalRow } from '../approvals'

const kindText: Record<ApprovalRow['kind'], string> = { 'change-request': 'Change request', 'app-trust': 'App to approve' }

/** A change request's decision: a note and the two buttons, each off with its reason; the Store decides and its sentence comes back when it refuses (SC21c). */
function Decision({ row, onDecided }: { row: ApprovalRow; onDecided: () => void }) {
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [refusal, setRefusal] = useState<string | null>(null)
  const send = async (approve: boolean) => {
    setBusy(true); setRefusal(null)
    try { await decideRequest(row, approve, note); setNote(''); onDecided() } catch (e) { setRefusal(e instanceof Error ? e.message : String(e)) } finally { setBusy(false) }
  }
  const approveWhy = busy ? 'Sending...' : decisionProblem(row, true, note)
  const rejectWhy = busy ? 'Sending...' : decisionProblem(row, false, note)
  return (
    <div style={{ gridColumn: '1 / -1' }} data-testid="approvals-decision">
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <input className="input" style={{ flex: 1, minWidth: 200 }} aria-label="Note" placeholder="Note (needed to reject)" value={note} onChange={(e) => setNote(e.target.value)} data-testid="approvals-note" />
        <button type="button" className="btn btn-primary" disabled={approveWhy !== null} title={approveWhy ?? undefined} data-testid="approvals-approve" onClick={() => send(true)}>Approve</button>
        <button type="button" className="btn" disabled={rejectWhy !== null} title={rejectWhy ?? undefined} data-testid="approvals-reject" onClick={() => send(false)}>Reject</button>
        {rejectWhy && !busy && note.trim() === '' && <span className="hint" data-testid="approvals-why">{rejectWhy}</span>}
      </div>
      {refusal && <div className="banner error" role="alert" style={{ marginTop: 6 }} data-testid="approvals-refusal"><span className="mark">!</span><span>{refusal}</span></div>}
    </div>
  )
}

/**
 * What waits for a decision on the servers of the suite, in one list: the change requests of the Store's models (an agent's write, or a model that asks for approval) and the app versions of a Gateway that ask to be trusted and
 * have no approval for their exact files. The longest wait is first, and an agent's write comes before a person's. A row says whether you may decide it and opens the screen where it is read and decided; a change request is approved or rejected here (SC21c: the note, then the Store's own rules decide), and the proposed rows are read on the Store's screen the row opens; an app version is decided on the Gateway's screen. Asked of each server through the relay every thirty seconds; a server that does not answer is named above the list, and the others still show.
 */
export function ApprovalsPage() {
  const client = useQueryClient()
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
                <div key={row.key} role="row" className="grid-row auto" style={{ gridTemplateColumns: '1.2fr 1fr 1.8fr 2.6fr 1.2fr 0.9fr 1fr', gap: 14 }} data-testid="approvals-row" data-kind={row.kind} data-can-decide={row.canDecide}>
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
                  {row.request && row.canDecide && <Decision row={row} onDecided={() => client.invalidateQueries({ queryKey: ['approvals'] })} />}
                </div>
              ))}
            </div>
          </>
        )}
      </div>
    </ConsoleShell>
  )
}
