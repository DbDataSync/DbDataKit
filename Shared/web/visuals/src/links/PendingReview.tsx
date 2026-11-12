import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { batchForecastText, batchOutcomeText, newIdempotencyKey, type ActionForecast, type ActionHost, type BatchOutcome, type BatchProblem } from './actions'
import { groupLines, type PendingStore, type StagedChange } from './pending'

/** The bar under the page while a set is pending: how many changes, and the two things to do with them. The pending set is the shell's: a widget cannot confirm it. */
export function PendingBar({ lines, onReview, onDiscard }: { lines: readonly StagedChange[]; onReview: () => void; onDiscard: () => void }) {
  if (lines.length === 0) return null
  return (
    <div className="vz-pending-bar" role="status" data-testid="vz-pending-bar" data-count={lines.length}>
      <span data-testid="vz-pending-count">{lines.length} change{lines.length === 1 ? '' : 's'}</span>
      <button type="button" className="vz-button vz-button-primary" data-testid="vz-pending-review" onClick={onReview}>Review and submit</button>
      <button type="button" className="vz-button" data-testid="vz-pending-discard" onClick={onDiscard}>Discard</button>
    </div>
  )
}

/**
 * The **one review popup** of the pending set (plan 32 DBR53b): every staged change grouped by entity (the row's label, the fields and the values to be written, verbatim), a line that can be removed, what will happen to
 * **the set** (applied together in one transaction, or waiting for approval as one request of N changes), and one button that submits all of it with **one idempotency key**. A refusal is shown **against the lines** (the
 * Store's rule, the field, the reason) and the set stays to be fixed and submitted again; an accepted submit clears the set.
 */
export function PendingReview({ store, host, onClose, onSubmitted }: {
  store: PendingStore
  host: ActionHost
  onClose: () => void
  /** The set was accepted (applied or waiting): the host resolves the widgets again. */
  onSubmitted: (outcome: BatchOutcome, widgets: string[]) => void
}) {
  const [lines, setLines] = useState<readonly StagedChange[]>(store.get())
  const [forecast, setForecast] = useState<ActionForecast | null | undefined>(undefined)
  const [busy, setBusy] = useState(false)
  const [outcome, setOutcome] = useState<BatchOutcome | null>(null)
  const key = useRef(newIdempotencyKey())
  const safe = useRef<HTMLButtonElement>(null)
  const opener = useRef<Element | null>(typeof document === 'undefined' ? null : document.activeElement)
  const accepted = useRef(onSubmitted)
  accepted.current = onSubmitted
  useEffect(() => store.subscribe(() => setLines(store.get())), [store])
  useEffect(() => {
    safe.current?.focus()
    const back = opener.current
    return () => { if (back instanceof HTMLElement && back.isConnected) back.focus() }
  }, [])
  const request = useMemo(() => ({ lines: lines.map((l) => l.request), idempotencyKey: key.current }), [lines])
  useEffect(() => {
    if (!host.forecastBatch || lines.length === 0) { setForecast(null); return }
    const controller = new AbortController()
    host.forecastBatch(request, controller.signal).then((f) => setForecast(f), () => setForecast(null))
    return () => controller.abort()
  }, [host, request, lines.length])

  const groups = useMemo(() => groupLines(lines), [lines])
  const indexOf = useMemo(() => new Map(lines.map((l, i) => [l.id, i])), [lines])
  const problems: BatchProblem[] = outcome && outcome.status !== 'applied' && outcome.status !== 'waiting' ? outcome.problems : []
  const finished = outcome?.status === 'applied' || outcome?.status === 'waiting'
  const failed = outcome && !finished ? outcome : null

  const submit = async () => {
    if (busy || finished || lines.length === 0 || !host.submit) return
    setBusy(true)
    setOutcome(null)
    let result: BatchOutcome
    try { result = await host.submit(request) }
    catch (e) { result = { status: 'failed', message: e instanceof Error ? e.message : String(e), problems: [] } }
    setBusy(false)
    setOutcome(result)
    if (result.status === 'applied' || result.status === 'waiting') {
      const widgets = [...new Set(store.get().map((l) => l.widget))]
      store.clear()
      accepted.current(result, widgets)
    }
  }
  const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); onClose() } }

  return (
    <div className="vz-surface vz-surface-dialog" data-testid="vz-pending" onKeyDown={onKey}>
      <div className="vz-surface-scrim" onClick={onClose} />
      <div className="vz-surface-box vz-action-box" role="dialog" aria-modal="true" aria-labelledby="vz-pending-title">
        <header className="vz-surface-head"><h2 id="vz-pending-title" data-testid="vz-pending-title">{finished ? 'Submitted' : `Review ${lines.length} change${lines.length === 1 ? '' : 's'}`}</h2></header>
        <div className="vz-action-body">
          {finished ? null : <p className="vz-action-forecast" data-testid="vz-pending-forecast" data-will={forecast?.will ?? 'unknown'}>{batchForecastText(lines.length, forecast)}</p>}
          {finished ? null : groups.map((g) => (
            <section key={g.entity} data-testid={`vz-pending-entity-${g.entity}`}>
              <h3 className="vz-pending-entity">{g.entity}</h3>
              <table className="vz-action-writes">
                <tbody>
                  {g.rows.flatMap((r) => r.lines.map((l) => {
                    const i = indexOf.get(l.id)!
                    const mine = problems.filter((p) => p.line === i)
                    return (
                      <tr key={l.id} data-testid={`vz-pending-line-${i}`} data-problem={mine.length > 0 ? '' : undefined}>
                        <th scope="row">{l.label}<br /><small>{l.operation}{l.key.length > 0 ? ` ${l.key.map((k) => k.value).join(', ')}` : ''}{l.version !== null ? `, version ${l.version}` : ''}</small></th>
                        <td>
                          {l.writes.length > 0 ? l.writes.map((w) => <div key={w.name}>{w.name}: {w.value}</div>) : <em>{l.operation === 'delete' ? 'deletes the row' : 'nothing written'}</em>}
                          {mine.map((p, j) => <div key={j} role="alert" className="vz-action-problem" data-testid={`vz-pending-problem-${i}`}>{p.attribute ? `${p.attribute}: ` : ''}{p.message}</div>)}
                        </td>
                        <td><button type="button" className="vz-button" data-testid={`vz-pending-remove-${i}`} aria-label={`Remove ${l.label} from the set`} disabled={busy} onClick={() => store.remove(l.id)}>Remove</button></td>
                      </tr>
                    )
                  }))}
                </tbody>
              </table>
            </section>
          ))}
          {failed ? (
            <div role="alert" className="vz-action-problem" data-testid="vz-pending-failed" data-status={failed.status}>
              <p>{batchOutcomeText(failed)}</p>
              {problems.filter((p) => p.line === null).length > 0 ? <ul>{problems.filter((p) => p.line === null).map((p, i) => <li key={i}>{p.attribute ? `${p.attribute}: ` : ''}{p.message}</li>)}</ul> : null}
              <p>The set is kept: fix what is named, or remove the line, and submit again.</p>
            </div>
          ) : null}
          {finished ? <p role="status" className="vz-action-done" data-testid="vz-pending-done" data-status={outcome.status}>{batchOutcomeText(outcome)}</p> : null}
          {!host.submit ? <p role="alert" className="vz-action-problem">This host cannot submit a set of changes.</p> : null}
          <footer className="vz-action-buttons">
            <button type="button" ref={safe} className="vz-button" data-testid="vz-pending-close" onClick={onClose}>{finished ? 'Close' : 'Back'}</button>
            {finished ? null : <button type="button" className="vz-button vz-button-primary" data-testid="vz-pending-submit" disabled={busy || lines.length === 0 || !host.submit} onClick={() => void submit()}>{busy ? 'Working' : `Submit ${lines.length} change${lines.length === 1 ? '' : 's'}`}</button>}
          </footer>
        </div>
      </div>
    </div>
  )
}
