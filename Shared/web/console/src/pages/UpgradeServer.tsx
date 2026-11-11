import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { availableKey, newestLine, type ServerAvailability } from '../upgradeApi'
import {
  isFinished, isRunning, offersUpgrade, outcomeSentence, progressLines, upgradeRunApi,
  type PrepareReport, type UpgradeStatus,
} from '../upgradeRun'

/** What `prepare` found, as the person reads it before applying: what was checked, what will happen, or the command for an install the console does not upgrade. */
export function PrepareReportView({ name, report }: { name: string; report: PrepareReport }) {
  return (
    <div className="card" style={{ margin: '8px 0' }} data-testid={`upgrade-report-${name}`} data-state={report.state}>
      <div className="card-body">
        <div data-testid={`upgrade-report-message-${name}`}>{report.message}</div>
        {report.artifact && (
          <div className="hint" data-testid={`upgrade-report-artifact-${name}`}>
            {report.artifact.name} ({Math.round(report.artifact.size / 1024)} KB) {report.artifact.verified ? 'matches the release’s SHA256SUMS' : 'was not checked'}: <span className="mono">{report.artifact.sha256.slice(0, 16)}…</span>
          </div>
        )}
        {report.generation_of_release != null && <div className="hint">The release is compatibility generation {report.generation_of_release}; this server is generation {report.generation}.</div>}
        {(report.problems ?? []).map((p) => <div key={p} className="hint" data-testid={`upgrade-report-problem-${name}`} data-tone="bad">{p}</div>)}
        {report.steps && (
          <ol className="hint" data-testid={`upgrade-report-steps-${name}`}>
            {report.steps.map((s) => <li key={s}>{s}</li>)}
          </ol>
        )}
        {report.instruction && (
          <div data-testid={`upgrade-instruction-${name}`}>
            <div className="hint">{report.instruction.text}</div>
            {report.instruction.command && <pre className="mono" data-testid={`upgrade-command-${name}`}>{report.instruction.command}</pre>}
          </div>
        )}
      </div>
    </div>
  )
}

/** The drain, the swap, the health wait and the result, line by line. Draws what it is given; `ServerUpgrade` asks the server. */
export function ProgressView({ name, status, unreachable }: { name: string; status: UpgradeStatus; unreachable: boolean }) {
  const lines = progressLines(status)
  const outcome = outcomeSentence(status)
  return (
    <div className="card" style={{ margin: '8px 0' }} data-testid={`upgrade-progress-${name}`} data-state={status.state}>
      <div className="card-body">
        <strong data-testid={`upgrade-state-${name}`}>{status.state.replace('_', ' ')}</strong>
        <ul className="hint">
          {lines.map((l) => <li key={l.key} data-testid={`upgrade-step-${name}-${l.key}`} data-state={l.state}>{l.state === 'done' ? '✓' : l.state === 'running' ? '…' : l.state === 'failed' ? '✗' : '·'} {l.text}</li>)}
        </ul>
        {unreachable && isRunning(status.state) && <div className="hint" data-testid={`upgrade-restarting-${name}`}>The server does not answer while it is swapped and restarted; this screen keeps asking.</div>}
        {outcome && <div data-testid={`upgrade-outcome-${name}`} data-tone={status.state === 'committed' ? 'ok' : 'bad'}>{outcome}</div>}
        {status.plan?.reason && status.state !== 'committed' && <div className="hint" data-testid={`upgrade-reason-${name}`}>{status.plan.reason}</div>}
      </div>
    </div>
  )
}

export function ServerUpgrade({ row }: { row: ServerAvailability }) {
  const queryClient = useQueryClient()
  const offers = offersUpgrade(row.product)
  const [report, setReport] = useState<PrepareReport | null>(null)
  const [confirming, setConfirming] = useState(false)
  const [started, setStarted] = useState(false)
  // asked again every two seconds while the helper works; an error is the server restarting, not a failure of the screen
  const status = useQuery({
    queryKey: ['upgrades', 'status', row.id],
    queryFn: () => upgradeRunApi.status(row.id),
    enabled: offers,
    retry: false,
    refetchInterval: (query) => (started && !isFinished(query.state.data?.state) || isRunning(query.state.data?.state) ? 2000 : false),
  })
  const prepare = useMutation({
    mutationFn: () => upgradeRunApi.prepare(row.id),
    onSuccess: (r) => { setReport(r); setConfirming(false); setStarted(false) },
  })
  const apply = useMutation({
    mutationFn: () => upgradeRunApi.apply(row.id),
    onSuccess: async (s) => {
      setConfirming(false); setStarted(true)
      queryClient.setQueryData(['upgrades', 'status', row.id], s)
      await queryClient.invalidateQueries({ queryKey: availableKey })
    },
  })
  const running = isRunning(status.data?.state)
  const current = status.data
  return (
    <div className="card" style={{ marginBottom: 12 }} data-testid={`upgrade-server-${row.name}`}>
      <div className="card-head">
        <span className="card-title">{row.name}</span>
        <span className="mono hint" data-testid={`upgrade-versions-${row.name}`}>{row.product ?? '—'} {row.version ?? ''}{row.generation ? ` (generation ${row.generation})` : ''}</span>
        <span data-testid={`upgrade-newest-${row.name}`} data-newer={row.newerAvailable ? 'true' : 'false'}>{newestLine(row)}</span>
        {row.notesUrl && <a href={row.notesUrl} target="_blank" rel="noreferrer" data-testid={`upgrade-notes-${row.name}`}>Release notes</a>}
      </div>
      <div className="card-body">
        {row.outsideAfterRelease && <div className="hint" data-testid={`upgrade-outside-${row.name}`} data-tone="bad">Outside N and N-1 after this release: {row.outsideAfterRelease}</div>}
        {!offers && <div className="hint" data-testid={`upgrade-unsupported-${row.name}`}>This kind of server is not upgraded from the console yet: use its install script or installer.</div>}
        {offers && current && !current.can_apply && <div className="hint" data-testid={`upgrade-cannot-${row.name}`}>The console does not upgrade this install: {current.reason}. Check shows the command.</div>}
        <ErrorBanner error={prepare.error ?? apply.error} />
        {offers && (
          <div className="row" style={{ gap: 8 }}>
            <button type="button" className="btn" disabled={prepare.isPending || running} onClick={() => prepare.mutate()} data-testid={`upgrade-check-${row.name}`}>{prepare.isPending ? 'Checking…' : 'Check'}</button>
            {report?.can_apply && !running && !confirming && (
              <button type="button" className="btn btn-primary" onClick={() => setConfirming(true)} data-testid={`upgrade-apply-${row.name}`}>Upgrade to {report.to}</button>
            )}
            {confirming && (
              <>
                <span className="hint">The server stops taking new work, finishes what is running, restarts on {report?.to}, and goes back if the new version does not become healthy.</span>
                <button type="button" className="btn btn-primary" disabled={apply.isPending} onClick={() => apply.mutate()} data-testid={`upgrade-apply-confirm-${row.name}`}>{apply.isPending ? 'Starting…' : 'Apply now'}</button>
                <button type="button" className="btn" onClick={() => setConfirming(false)} data-testid={`upgrade-apply-cancel-${row.name}`}>Cancel</button>
              </>
            )}
          </div>
        )}
        {report && !started && <PrepareReportView name={row.name} report={report} />}
        {current && (started || isRunning(current.state) || isFinished(current.state)) && (
          <ProgressView name={row.name} status={current} unreachable={status.isError} />
        )}
      </div>
    </div>
  )
}

