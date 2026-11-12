import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { checkAnswers, resolveAction, type ActionInfo } from '../views/actions'
import type { LinkInfo, LinkParams, LinkRow } from '../views/links'
import { forecastText, newIdempotencyKey, outcomeText, type ActionForecast, type ActionHost, type ActionOutcome } from './actions'

/** An action the shell was asked to confirm: the link read, the widget and the row the click came from. */
export interface OpenAction { widget: string; link: LinkInfo & { action: ActionInfo }; row: LinkRow }

/**
 * The shell's confirmation popup for an action (plan 32 DBR53). **Its words are the declaration's**: the title and the `show` lines come from the link's `confirm` with the row's values as plain text, and everything
 * that will be written is listed **verbatim** in a table, so a widget (which only names the link and gives a row) cannot dress one request as another. The page behind is `inert` (the host sets it); focus starts on the
 * safe button (Cancel for a delete, so Enter does not confirm it) and returns to what had it. The idempotency key is made when the popup opens, and the confirm button is disabled while the call is out, so a double click
 * is one change. The outcome is shown in the popup: applied, waiting for approval (the Store's 202), started, or refused with the server's reasons by field; a refusal leaves the popup open to be fixed or cancelled.
 */
export function ActionDialog({ open, parameters, host, onDone, onCancel }: {
  open: OpenAction
  parameters: LinkParams
  host: ActionHost
  /** The call was accepted (applied, waiting or started): the host closes the popup and resolves what `then` says. */
  onDone: (outcome: ActionOutcome) => void
  onCancel: () => void
}) {
  const { link } = open
  const action = link.action
  const [answers, setAnswers] = useState<Record<string, string | undefined>>(() => Object.fromEntries(Object.values(action.ask).filter((a) => a.default !== undefined).map((a) => [a.name, a.default])))
  const [forecast, setForecast] = useState<ActionForecast | null | undefined>(undefined)
  const [busy, setBusy] = useState(false)
  const [outcome, setOutcome] = useState<ActionOutcome | null>(null)
  const [showProblems, setShowProblems] = useState(false)
  const key = useRef(newIdempotencyKey())
  const opener = useRef<Element | null>(typeof document === 'undefined' ? null : document.activeElement)
  const safe = useRef<HTMLButtonElement>(null)
  const done = useRef(onDone)
  done.current = onDone

  const resolved = useMemo(() => resolveAction(link.name, { action, label: link.label }, open.row, parameters, answers), [link, action, open.row, parameters, answers])
  const problems = useMemo(() => checkAnswers(resolved.asks, answers), [resolved.asks, answers])
  const blocked = resolved.missing.length > 0

  useEffect(() => {
    safe.current?.focus()
    const returnTo = opener.current
    return () => { if (returnTo instanceof HTMLElement && returnTo.isConnected) returnTo.focus() }
  }, [])
  useEffect(() => {
    if (!host.forecast) { setForecast(null); return }
    const controller = new AbortController()
    host.forecast(link.name, controller.signal).then((f) => setForecast(f), () => setForecast(null))
    return () => controller.abort()
  }, [host, link.name])

  const confirm = async () => {
    if (busy || blocked || outcome?.status === 'applied' || outcome?.status === 'waiting' || outcome?.status === 'started') return
    if (Object.keys(problems).length > 0) { setShowProblems(true); return }
    setBusy(true)
    setOutcome(null)
    let result: ActionOutcome
    try { result = await host.run({ link: link.name, widget: open.widget, row: open.row, parameters, answers, idempotencyKey: key.current }) }
    catch (e) { result = { status: 'failed', message: e instanceof Error ? e.message : String(e) } }
    setBusy(false)
    setOutcome(result)
    if (result.status === 'applied' || result.status === 'waiting' || result.status === 'started') done.current(result)
  }

  const onKey = (e: KeyboardEvent) => {
    if (e.key === 'Escape') { e.stopPropagation(); onCancel() }
    // Enter confirms a change in a field of the popup, but never a delete
    else if (e.key === 'Enter' && resolved.destructive) e.preventDefault()
  }
  const failed = outcome && outcome.status !== 'applied' && outcome.status !== 'waiting' && outcome.status !== 'started' ? outcome : null
  const finished = outcome?.status === 'applied' || outcome?.status === 'waiting' || outcome?.status === 'started'
  const violations = failed?.status === 'refused' ? failed.violations ?? [] : []

  return (
    <div className="vz-surface vz-surface-dialog" data-testid="vz-action" onKeyDown={onKey}>
      <div className="vz-surface-scrim" data-testid="vz-action-scrim" onClick={onCancel} />
      <div className="vz-surface-box vz-action-box" role="dialog" aria-modal="true" aria-labelledby="vz-action-title" data-destructive={resolved.destructive ? '' : undefined}>
        <header className="vz-surface-head">
          <h2 id="vz-action-title" data-testid="vz-action-title">{resolved.title}</h2>
        </header>
        <form className="vz-action-body" onSubmit={(e) => { e.preventDefault(); if (!resolved.destructive) void confirm() }}>
          <p className="vz-action-forecast" data-testid="vz-action-forecast" data-will={forecast?.will ?? 'unknown'}>{forecastText(resolved.kind, forecast)}</p>
          {resolved.shows.length > 0 ? (
            <dl className="vz-action-shows" data-testid="vz-action-shows">
              {resolved.shows.map((s) => (<div key={s.field}><dt>{s.field}</dt><dd>{s.value}</dd></div>))}
            </dl>
          ) : null}
          {resolved.asks.map((a) => (
            <label key={a.name} className="vz-action-ask">
              <span>{a.label}{a.required ? '' : ' (optional)'}</span>
              {a.choices.length > 0 ? (
                <select data-testid={`vz-action-ask-${a.name}`} value={answers[a.name] ?? ''} onChange={(e) => setAnswers({ ...answers, [a.name]: e.target.value })} disabled={busy || finished}>
                  <option value="">{a.required ? 'Choose' : 'None'}</option>
                  {a.choices.map((c) => <option key={c} value={c}>{c}</option>)}
                </select>
              ) : a.type === 'boolean' ? (
                <select data-testid={`vz-action-ask-${a.name}`} value={answers[a.name] ?? ''} onChange={(e) => setAnswers({ ...answers, [a.name]: e.target.value })} disabled={busy || finished}>
                  <option value="">{a.required ? 'Choose' : 'None'}</option><option value="true">true</option><option value="false">false</option>
                </select>
              ) : (
                <input data-testid={`vz-action-ask-${a.name}`} type={a.type === 'date' ? 'date' : 'text'} inputMode={a.type === 'integer' || a.type === 'number' ? 'decimal' : undefined} value={answers[a.name] ?? ''} onChange={(e) => setAnswers({ ...answers, [a.name]: e.target.value })} disabled={busy || finished} />
              )}
              {showProblems && problems[a.name] ? <em role="alert" className="vz-action-problem">{problems[a.name]}</em> : null}
            </label>
          ))}
          <table className="vz-action-writes" data-testid="vz-action-writes">
            <caption>{resolved.operation === 'run' ? `Inputs of ${resolved.target}` : `${resolved.operation === 'insert' ? 'A new row of' : resolved.operation === 'delete' ? 'Deletes a row of' : 'Changes a row of'} ${resolved.target}`}</caption>
            <tbody>
              {resolved.key.map((k) => (<tr key={`k-${k.name}`}><th scope="row">{k.name === 'key' ? 'row key' : `key ${k.name}`}</th><td>{k.value}</td></tr>))}
              {resolved.version !== null ? <tr><th scope="row">version</th><td>{resolved.version}</td></tr> : null}
              {resolved.writes.map((w) => (<tr key={`w-${w.name}`} data-testid={`vz-action-write-${w.name}`}><th scope="row">{w.name}</th><td>{w.value}</td></tr>))}
              {resolved.key.length + resolved.writes.length === 0 ? <tr><td colSpan={2}>nothing is written</td></tr> : null}
            </tbody>
          </table>
          {blocked ? <p role="alert" className="vz-action-problem" data-testid="vz-action-missing">The row does not give {resolved.missing.join(', ')}, so this cannot be sent.</p> : null}
          {failed ? (
            <div role="alert" className="vz-action-problem" data-testid="vz-action-failed" data-status={failed.status}>
              <p>{outcomeText(failed)}</p>
              {violations.length > 0 ? <ul>{violations.map((v, i) => <li key={i}>{v.attribute ? `${v.attribute}: ` : ''}{v.message}</li>)}</ul> : null}
            </div>
          ) : null}
          {finished ? <p role="status" className="vz-action-done" data-testid="vz-action-done" data-status={outcome.status}>{outcomeText(outcome)}</p> : null}
          <footer className="vz-action-buttons">
            <button type="button" ref={safe} className="vz-button" data-testid="vz-action-cancel" onClick={onCancel}>{finished ? 'Close' : 'Cancel'}</button>
            {finished ? null : (
              <button type="button" className={`vz-button vz-button-primary${resolved.destructive ? ' vz-button-danger' : ''}`} data-testid="vz-action-confirm" disabled={busy || blocked} onClick={() => void confirm()}>{busy ? 'Working' : resolved.button}</button>
            )}
          </footer>
        </form>
      </div>
    </div>
  )
}
