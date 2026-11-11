import { useMutation } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type CheckResult } from '../api'
import { checkLine, checkVerdict } from '../model'

/** A check's findings, drawn without a query so a test can render them: the verdict first, then each finding with its code and place; a file name opens that file when the screen says how. */
export function CheckResultView({ result, onOpen }: { result: CheckResult; onOpen?: (path: string) => void }) {
  const verdict = checkVerdict(result)
  return (
    <div data-testid="wsp-check-result" data-ok={String(verdict.ok)} data-kind={result.kind}>
      <div className={verdict.ok ? 'hint' : 'error'} data-testid="wsp-check-verdict">{verdict.text}</div>
      <ul style={{ listStyle: 'none', padding: 0, margin: '8px 0 0' }}>
        {result.diagnostics.map((d, i) => (
          <li key={i} data-testid="wsp-check-finding" data-severity={d.severity} data-code={d.code} className={d.severity === 'error' ? 'error' : 'hint'}>
            {d.file && onOpen
              ? <><button type="button" className="btn-link mono" data-testid="wsp-check-open" onClick={() => onOpen(d.file!)}>{d.file}{d.line ? `:${d.line}` : ''}</button> {d.code}: {d.message}</>
              : checkLine(d)}
          </li>
        ))}
      </ul>
    </div>
  )
}

/** The Check tab of a workspace: runs the draft's kind's own validators (the ones its product runs) on what is saved in the draft, and shows what they found. Nothing is written. */
export function CheckPanel({ project, id, onOpen }: { project: string; id: string; onOpen?: (path: string) => void }) {
  const check = useMutation({ mutationFn: () => workspaceApi.check(project, id) })
  return (
    <div data-testid="wsp-check">
      <div className="row" style={{ gap: 8, alignItems: 'center' }}>
        <button type="button" className="btn btn-primary" disabled={check.isPending} data-testid="wsp-check-run" onClick={() => check.mutate()}>{check.isPending ? 'Checking...' : 'Check the draft'}</button>
        <span className="hint">Saved files only; a commit is not needed.</span>
      </div>
      <ErrorBanner error={check.error} />
      {check.data && <CheckResultView result={check.data} onOpen={onOpen} />}
    </div>
  )
}
