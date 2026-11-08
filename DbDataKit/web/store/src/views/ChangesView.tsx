import type { ChangeRequestRow, ProposedChange, RequestList, Violation } from '../api'
import { actionText, cellText, changeSummary, decideState, fieldDiff, keyText, proposerText, sortRequests, stateText } from '../model'
import { Gated } from './ModelEditorView'

export const FILTERS = [['pending', 'Waiting'], ['approved', 'Approved'], ['rejected', 'Rejected'], ['', 'All']] as const

export function RequestRows({ rows, selected, onSelect, filter, onFilter }: { rows: ChangeRequestRow[]; selected: string | null; onSelect: (id: string) => void; filter: string; onFilter: (state: string) => void }) {
  return (
    <div data-testid="changes-list">
      <div className="subtabbar" data-testid="changes-filters">
        {FILTERS.map(([state, label]) => (
          <button key={label} type="button" className={`subtab ${filter === state ? 'active' : ''}`} onClick={() => onFilter(state)} data-testid={`changes-filter-${state || 'all'}`}>{label}</button>
        ))}
      </div>
      {rows.length === 0 && <div className="empty" data-testid="changes-empty">{filter === 'pending' ? 'Nothing is waiting for a decision.' : 'No change requests here.'}</div>}
      {sortRequests(rows).map((r) => (
        <button key={r.id} type="button" className={`card ${selected === r.id ? 'selected' : ''}`} style={{ display: 'block', width: '100%', textAlign: 'left', cursor: 'pointer' }} onClick={() => onSelect(r.id)} data-testid="change-row" data-state={r.state} data-selected={selected === r.id}>
          <div className="card-body">
            <div className="row" style={{ gap: 10, alignItems: 'baseline', flexWrap: 'wrap' }}>
              <span className="card-title" data-testid="change-row-state">{stateText(r.state)}</span>
              <span data-testid="change-row-proposer">{proposerText(r)}</span>
              <span className="hint">{new Date(r.proposed_at).toLocaleString()}</span>
              <span className="hint">{changeSummary(undefined, r.change_count)}</span>
            </div>
            {r.description && <div data-testid="change-row-description">{r.description}</div>}
          </div>
        </button>
      ))}
    </div>
  )
}

/** One proposed change: what it does, to which row, and per attribute what the row holds now against what it would hold. */
export function ChangeView({ index, change, current, loading, note }: { index: number; change: ProposedChange; current: Record<string, unknown> | null; loading: boolean; note?: string | null }) {
  const diff = fieldDiff(change, current)
  return (
    <div className="card" data-testid="change-item" data-action={change.action}>
      <div className="card-head"><span className="card-title">{index + 1}. {actionText(change.action)} <span className="mono">{change.entity}</span>{change.key ? <span className="mono"> ({keyText(change.key)})</span> : null}</span></div>
      <div className="card-body">
        {loading && <div className="empty" data-testid="change-loading">Reading the row as it is now...</div>}
        {note && <div className="hint" data-testid="change-note">{note}</div>}
        <table className="table" data-testid="change-diff">
          <thead><tr><th>Attribute</th><th>Now</th><th>After</th></tr></thead>
          <tbody>
            {diff.map((f) => (
              <tr key={f.name} data-testid="change-field" data-changed={f.changed}>
                <td className="mono">{f.name}</td>
                <td data-testid="change-before">{cellText(f.before)}</td>
                <td data-testid="change-after" style={f.changed ? { fontWeight: 600 } : undefined}>{cellText(f.after)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {change.expected_version !== undefined && <div className="hint">Quotes version {change.expected_version} of the row.</div>}
      </div>
    </div>
  )
}

export interface DecisionProps {
  list: RequestList | undefined
  request: ChangeRequestRow
  note: string
  onNote: (note: string) => void
  busy: boolean
  /** The signed-in person's id, to say when the request is theirs. */
  me: string | null
  onApprove: () => void
  onReject: () => void
  refusal: string | null
  /** What the Store said was broken, when it refused to apply the request: each rule, with the attribute it is about. */
  violations?: Violation[]
}

/** Approve and reject with a note. Reject needs the reason written (the proposer reads it); both say why they are off. */
export function DecisionView(p: DecisionProps) {
  const base = decideState(p.list, p.request, p.busy)
  const rejectReason = base.allowed && p.note.trim() === '' ? 'Say why in the note: the person who proposed it reads it.' : base.reason
  const decided = p.request.state !== 'pending'
  return (
    <div className="card" data-testid="change-decision" data-state={p.request.state}>
      <div className="card-body">
        {decided && (
          <p data-testid="change-decided">{stateText(p.request.state)}{p.request.decided_by ? ` by ${p.request.decided_by}` : ''}{p.request.decided_at ? ` on ${new Date(p.request.decided_at).toLocaleString()}` : ''}{p.request.note ? `: ${p.request.note}` : '.'}</p>
        )}
        {!decided && (
          <>
            {p.me !== null && p.me === p.request.proposed_by && <p className="hint" data-testid="change-own">You proposed this request. The Store lets you decide it; a model that wants a second person says so with <code>approval: owner</code>.</p>}
            <div className="field">
              <label htmlFor="change-note">Note</label>
              <textarea id="change-note" className="input" rows={2} value={p.note} onChange={(e) => p.onNote(e.target.value)} placeholder="Required to reject, optional to approve" data-testid="change-note-input" />
            </div>
            <div className="row" style={{ gap: 14, flexWrap: 'wrap' }}>
              <Gated testId="change-approve" label="Approve and apply" className="btn btn-primary" allowed={base.allowed} reason={base.reason} onClick={p.onApprove} />
              <Gated testId="change-reject" label="Reject" className="btn btn-danger" allowed={base.allowed && p.note.trim() !== ''} reason={rejectReason} onClick={p.onReject} />
            </div>
          </>
        )}
        {p.refusal && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="change-refusal"><span className="mark">!</span><span>{p.refusal}</span></div>}
        {p.violations && p.violations.length > 0 && (
          <ul style={{ marginTop: 6 }} data-testid="change-violations">
            {p.violations.map((v, i) => <li key={i} data-testid="change-violation" data-rule={v.rule}><span className="mono">{v.rule}</span> {v.message}</li>)}
          </ul>
        )}
        {p.refusal && p.request.state === 'pending' && <p className="hint" data-testid="change-stays">The request stays waiting; nothing was written.</p>}
      </div>
    </div>
  )
}
