import { Field } from '@dbdatakit/shell'
import type { ConnectionConfig, ConnectionInput, ConnectionTestReport } from '../../api/types'

/**
 * A connection's two logins (plan 28c, E20). The login of the form above is the <b>read</b> login: what this server uses to browse,
 * test and preview, and it serves a replication as well when nothing else is set. A second login is the <b>run</b> login: the one a
 * pass connects with, reading the source and writing the target, and the one a runner is given. A password is never shown; blank
 * keeps the stored one.
 */
export function RunLoginCard({ draft, onChange, existing, testReport, testPending, testError, onTest }: {
  draft: ConnectionInput
  onChange: (next: ConnectionInput) => void
  existing: ConnectionConfig | undefined
  testReport: ConnectionTestReport | undefined
  testPending: boolean
  testError: Error | null
  onTest: (() => void) | undefined
}) {
  const on = draft.runLogin !== null && draft.runLogin !== undefined
  const set = (patch: Partial<NonNullable<ConnectionInput['runLogin']>>) =>
    onChange({ ...draft, runLogin: { ...(draft.runLogin ?? {}), ...patch } })

  return (
    <div className="card" data-testid="connection-run-login-card">
      <div className="card-head">
        <span className="card-title">Logins</span>
        <span className="card-note">read and run</span>
      </div>
      <div className="card-body">
        <span className="hint" style={{ lineHeight: 1.5 }}>
          The login above is the <b>read</b> login: this server browses, tests and previews with it. A replication pass reads and writes with the
          <b> run</b> login, which is the same login unless you give it another here. A runner is given the run login only.
        </span>
        <label className="row" style={{ gap: 8 }}>
          <input
            type="checkbox"
            checked={on}
            onChange={(e) => onChange({ ...draft, runLogin: e.target.checked ? { userId: '', password: '' } : null })}
            data-testid="connection-run-login-toggle"
          />
          <span>Use a different login for replications (run)</span>
        </label>
        {on && (
          <>
            <Field label="Run user">
              <input
                className="input"
                value={draft.runLogin?.userId ?? ''}
                placeholder={draft.userId ? `${draft.userId} (same as read)` : 'same as the read user'}
                onChange={(e) => set({ userId: e.target.value })}
                data-testid="connection-run-user-input"
              />
            </Field>
            <Field label="Run password">
              <input
                className="input"
                type="password"
                autoComplete="new-password"
                value={draft.runLogin?.password ?? ''}
                placeholder={existing?.hasRunLogin ? 'blank keeps the stored password' : ''}
                onChange={(e) => set({ password: e.target.value })}
                data-testid="connection-run-password-input"
              />
            </Field>
            {existing?.runCredentialSecretRef && (
              <Field label="Run secret">
                <input className="input mono" readOnly value={existing.runCredentialSecretRef} data-testid="connection-run-secret-ref" />
              </Field>
            )}
            {onTest && (
              <div className="row" style={{ gap: 8 }}>
                <button type="button" className="btn btn-chrome" onClick={onTest} disabled={testPending} data-testid="test-run-login-button">
                  {testPending ? 'Testing…' : 'Test run login'}
                </button>
                {testError && <span className="hint" style={{ color: 'var(--danger)' }}>{testError.message}</span>}
                {!testPending && testReport && (
                  <span className="status" data-testid="run-login-test-result">
                    <span className={`dot ${testReport.succeeded ? 'dot-ok' : 'dot-bad'}`} />
                    {testReport.succeeded ? 'run login works' : `run login failed${testReport.error ? `: ${testReport.error}` : ''}`}
                  </span>
                )}
              </div>
            )}
            <span className="hint">Saved connections are tested as they were last saved.</span>
          </>
        )}
      </div>
    </div>
  )
}
