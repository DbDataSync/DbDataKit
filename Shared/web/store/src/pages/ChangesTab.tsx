import { useState } from 'react'
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useAuthStatus } from '@dbdatakit/shell'
import { refusalOf, storeApi, storeClient, type Violation } from '../api'
import { ChangeView, DecisionView, RequestRows } from '../views/ChangesView'

/** The change requests of a model: the ones waiting, then the decided ones; read the proposed rows beside what the rows hold now, approve (the Store applies them) or reject with a reason. */
export function ChangesTab({ model }: { model: string }) {
  const queryClient = useQueryClient()
  const status = useAuthStatus()
  const [filter, setFilter] = useState('pending')
  const [selected, setSelected] = useState<string | null>(null)
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [refusal, setRefusal] = useState<string | null>(null)
  const [violations, setViolations] = useState<Violation[]>([])

  const list = useQuery({ queryKey: ['store', 'requests', model, filter], queryFn: () => storeApi.requests(model, null, filter || null), refetchInterval: 10_000 })
  const detail = useQuery({ queryKey: ['store', 'request', model, selected], queryFn: () => storeApi.changeRequest(model, null, selected!), enabled: selected !== null })
  const changes = detail.data?.changes ?? []
  // what each row holds now (an insert has none): the proposal is shown against it
  const rows = useQueries({
    queries: changes.map((c, i) => ({
      queryKey: ['store', 'request-row', model, selected, i],
      queryFn: async () => (c.action === 'insert' || !c.key ? null : (await storeClient.row(model, c.entity, c.key))?.values ?? null),
      retry: false,
    })),
  })

  const choose = (id: string) => { setSelected(id); setNote(''); setRefusal(null); setViolations([]) }
  const decide = async (approve: boolean) => {
    if (!selected) return
    setBusy(true); setRefusal(null); setViolations([])
    try {
      await storeApi.decide(model, null, selected, approve, note)
      setNote('')
      await queryClient.invalidateQueries({ queryKey: ['store'] })
    } catch (e) { const r = refusalOf(e); setRefusal(r.message); setViolations(r.violations) } finally { setBusy(false) }
  }

  return (
    <div data-testid="store-changes">
      <p className="hint">A write waits here when it is an agent's, or when the model asks for approval of changes. Approving applies the rows exactly as proposed, through the same rules; a request that breaks one stays waiting and says why.</p>
      <ErrorBanner error={list.error} />
      {list.data && <p className="hint" data-testid="changes-policy" data-can-decide={list.data.can_decide}>{list.data.can_decide ? `You may approve and reject on this model (it needs the ${list.data.decision_needs} role).` : `Deciding on this model needs the ${list.data.decision_needs} role; you may read the requests.`}</p>}
      <div className="row" style={{ gap: 18, alignItems: 'flex-start', flexWrap: 'wrap' }}>
        <div style={{ flex: '1 1 340px', minWidth: 300 }}>
          {list.isLoading ? <div className="empty" data-testid="changes-loading">Loading...</div> : <RequestRows rows={list.data?.requests ?? []} selected={selected} onSelect={choose} filter={filter} onFilter={(f) => { setFilter(f); setSelected(null) }} />}
        </div>
        <div style={{ flex: '2 1 460px', minWidth: 320 }} data-testid="change-detail">
          {!selected && <div className="empty" data-testid="change-detail-empty">Choose a request to read what it proposes.</div>}
          <ErrorBanner error={detail.error} />
          {detail.data && (
            <>
              <p data-testid="change-detail-head">{detail.data.description ?? 'No description.'} <span className="hint">({detail.data.change_count} change{detail.data.change_count === 1 ? '' : 's'}, request {detail.data.id.slice(0, 8)})</span></p>
              {changes.map((c, i) => {
                const q = rows[i]
                const gone = c.action !== 'insert' && !!c.key && q?.isSuccess && q.data === null
                return <ChangeView key={i} index={i} change={c} current={q?.data ?? null} loading={q?.isLoading ?? false} note={q?.isError ? 'The row could not be read now.' : gone ? 'This row does not exist now: applying the change will fail.' : null} />
              })}
              <DecisionView list={list.data} request={detail.data} note={note} onNote={setNote} busy={busy} me={status.data?.userId ?? null} onApprove={() => decide(true)} onReject={() => decide(false)} refusal={refusal} violations={violations} />
            </>
          )}
        </div>
      </div>
    </div>
  )
}
