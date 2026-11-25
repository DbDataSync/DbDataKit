import { Link as ConsoleLink } from 'react-router-dom'
import type { StagedRun } from '../api'

/**
 * The preflight of a package (Design: docs/publishing.md#preflight-before-activation): its read-only manual task `preflight`, whose parts each record the step
 * `preflight-findings` with their findings. The console runs it on a version it stages in production before it serves it, and Re-check on the Runs tab runs it on the
 * version the channel serves.
 */
export const PREFLIGHT_TASK = 'preflight'
export const PREFLIGHT_STEP = 'preflight-findings'

export type PreflightSeverity = 'error' | 'warning' | 'info'

/** One thing a preflight found: how serious, in words, and what it is about (a connection, a schema). */
export interface PreflightFinding { severity: PreflightSeverity; message: string; about?: string | null }

/** What the console says of the preflight of a deploy: the run and its address, whether it ended, the findings, and what became of the activation. */
export interface DeployPreflight {
  declared: boolean
  run?: string | null
  link?: string
  state?: 'running' | 'finished' | 'not-started'
  outcome?: string | null
  errors?: number
  warnings?: number
  findings?: PreflightFinding[]
  error?: string
  activation?: 'activated' | 'refused' | 'waiting' | 'already-served' | 'refused-by-runner'
  confirmed_by?: string
}

const severityOf = (s: unknown): PreflightSeverity => (s === 'warning' || s === 'info' ? s : 'error')

/** The findings the parts of a preflight run recorded, in order; a severity this screen does not know is read as an error, as the console reads it. */
export function preflightFindingsOf(run: StagedRun): PreflightFinding[] | null {
  const parts = (run.steps ?? []).map((s) => s.result as { part?: unknown; findings?: unknown } | null).filter((r): r is { part?: unknown; findings?: unknown } => !!r && typeof r === 'object' && Array.isArray((r as { findings?: unknown }).findings) && 'part' in r)
  if (parts.length === 0) return null
  return parts.flatMap((p) => (p.findings as Record<string, unknown>[]).map((f) => ({ severity: severityOf(f.severity), message: String(f.message ?? ''), about: typeof f.about === 'string' ? f.about : null })))
}

/** One part of a preflight run (a package names its parts: a Sync set's are `<replication>/<mapping>`, `<replication> tracking`) and what it found. */
export interface PreflightPart { part: string; findings: PreflightFinding[] }

/** The parts of a preflight run in the order they were recorded, each with its findings; null when the run recorded none (it failed, or is not a preflight). */
export function preflightPartsOf(run: StagedRun): PreflightPart[] | null {
  const parts = (run.steps ?? []).map((s) => ({ key: s.key, result: s.result as { part?: unknown; findings?: unknown } | null }))
    .filter((s) => !!s.result && typeof s.result === 'object' && Array.isArray(s.result.findings))
  if (parts.length === 0) return null
  return parts.map(({ key, result }) => ({
    part: typeof result!.part === 'string' ? result!.part : key,
    findings: (result!.findings as Record<string, unknown>[]).map((f) => ({ severity: severityOf(f.severity), message: String(f.message ?? ''), about: typeof f.about === 'string' ? f.about : null })),
  }))
}

/** The line that counts the findings. */
export function findingsSummary(findings: readonly PreflightFinding[]): string {
  const errors = findings.filter((f) => f.severity === 'error').length
  const warnings = findings.filter((f) => f.severity === 'warning').length
  if (errors === 0 && warnings === 0) return 'The preflight found nothing in the way.'
  return `The preflight found ${errors} error${errors === 1 ? '' : 's'} and ${warnings} warning${warnings === 1 ? '' : 's'}.`
}

/** The findings, one line each, errors first. */
export function PreflightFindings({ findings }: { findings: readonly PreflightFinding[] }) {
  const order: Record<PreflightSeverity, number> = { error: 0, warning: 1, info: 2 }
  const sorted = [...findings].sort((a, b) => order[a.severity] - order[b.severity])
  return (
    <ul style={{ margin: '4px 0', paddingLeft: 18 }} data-testid="wsp-preflight-findings">
      {sorted.map((f, i) => (
        <li key={i} className={f.severity === 'error' ? 'error' : f.severity === 'warning' ? '' : 'hint'} data-testid="wsp-preflight-finding" data-severity={f.severity}>
          <strong>{f.severity}</strong>{f.about ? <> <span className="mono">{f.about}</span></> : null}: {f.message}
        </li>
      ))}
    </ul>
  )
}

/** What the publish panel says of a deploy that waited for its preflight, and the person's way on: activate it once the run has ended, or activate it anyway after an error. */
export function preflightWords(p: DeployPreflight): string {
  if (!p.declared) return 'The package declares no preflight, so nothing was checked before it was served.'
  if (p.state === 'not-started') return `The preflight could not be started, so the version stays staged and was not activated: ${p.error ?? ''}`.trim()
  if (p.state === 'running') return 'The preflight is still running on the staged version. Activate it once the run has ended.'
  switch (p.activation) {
    case 'activated': return p.confirmed_by ? 'The version was activated although the preflight found errors; the confirmation is kept in the Runner\'s record of the activation.' : 'The preflight found no error, so the version was activated.'
    case 'already-served': return 'The version was served already; nothing was activated.'
    case 'waiting': return 'The Runner activates the version once its runs in flight have ended.'
    case 'refused-by-runner': return 'The Runner refused the activation; the version stays staged.'
    default: return 'The preflight found errors, so the version was not activated. It stays staged. Read the findings and the run; activating it anyway is recorded on the Runner.'
  }
}

/** The card of a deploy's preflight: the words, the counts, the findings, the run's link, and the activation it waits for. */
export function PreflightCard({ preflight, onActivate, busy }: { preflight: DeployPreflight; onActivate?: (confirm: boolean) => void; busy?: boolean }) {
  const p = preflight
  const warn = p.declared && (p.activation === 'refused' || p.state !== 'finished' || (p.errors ?? 0) > 0 || (p.warnings ?? 0) > 0)
  const canActivate = !!onActivate && p.declared && p.activation !== 'activated' && p.activation !== 'already-served'
  const needsConfirm = (p.errors ?? 0) > 0 || p.state === 'not-started'
  return (
    <div className={`banner ${warn ? 'warn' : ''}`} role="status" style={{ display: 'block', marginTop: 8 }} data-testid="wsp-preflight" data-state={p.state ?? 'none'} data-activation={p.activation ?? ''} data-errors={p.errors ?? 0}>
      <div data-testid="wsp-preflight-words">{preflightWords(p)}</div>
      {p.findings && p.findings.length > 0 && <>
        <div className="hint">{findingsSummary(p.findings)}</div>
        <PreflightFindings findings={p.findings} />
      </>}
      {p.link && <ConsoleLink to={p.link} data-testid="wsp-preflight-run-link">The preflight run and its log on the Runner</ConsoleLink>}
      {canActivate && (
        <div className="row" style={{ gap: 8, marginTop: 6 }}>
          <button type="button" className={`btn btn-sm ${needsConfirm ? 'btn-danger' : 'btn-primary'}`} disabled={busy} data-testid="wsp-preflight-activate" data-confirm={needsConfirm}
            onClick={() => onActivate!(needsConfirm)}>{needsConfirm ? 'Activate anyway' : 'Activate'}</button>
          {needsConfirm && <span className="hint">You confirm what the preflight found; the confirmation is kept with the activation.</span>}
        </div>
      )}
    </div>
  )
}
