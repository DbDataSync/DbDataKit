import type { CommandAnswer, Diagnostic } from '../types'

/** Why a command that did not run to the end says so: its diagnostics, its errors, or what it printed. */
export function Problems({ answer }: { answer: CommandAnswer<unknown> | undefined }) {
  if (!answer || answer.exit === 0) return null
  const diagnostics: Diagnostic[] = answer.document?.diagnostics ?? []
  const errors = answer.document?.errors ?? []
  return (
    <div className="card" style={{ marginBottom: 14 }} data-testid="build-command-problems">
      <div className="card-body">
        {diagnostics.map((d, i) => (
          <div key={`${d.code}-${i}`} data-testid="build-command-problem">
            <strong>{d.code} {d.title}</strong>
            <div className="hint">{d.found}</div>
            {d.fix && <div className="hint">Fix: {d.fix}</div>}
          </div>
        ))}
        {errors.map((e, i) => <div key={i} data-testid="build-command-problem">{e}</div>)}
        {diagnostics.length === 0 && errors.length === 0 && <div data-testid="build-command-problem">{answer.text ?? 'The command did not finish.'}</div>}
      </div>
    </div>
  )
}
