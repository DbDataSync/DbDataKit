import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import { choiceKey, dryRunBlock, measure, plansOf, refreshBody, refreshFailure, runBlock, summarize, type RefreshChoice } from '../refresh'
import type { CommandAnswer, RefreshPlanEntry, RefreshResult } from '../types'
import { Problems } from './Problems'

const wrap = { whiteSpace: 'normal', overflowWrap: 'anywhere' } as const

/** The answer of a refresh: how it ended, each load's state, what a dry run resolved, and the checks evaluated afterwards (`data.checks`, plan 23a FQ09). */
export function RefreshAnswerView({ answer, running }: { answer: CommandAnswer<RefreshResult> | undefined; running: boolean }) {
  if (running) return <div className="hint" data-testid="build-refresh-running">Running. The server answers when the run has ended; this can take a while, and the page may be left open.</div>
  if (!answer) return null
  const summary = summarize(answer)
  const data = answer.document?.data
  const checks = data?.checks
  return (
    <div className="card" style={{ marginTop: 14 }} data-testid="build-refresh-answer">
      <div className="card-body">
        {summary
          ? <div data-testid="build-refresh-headline" data-tone={summary.tone}><strong>{summary.headline}</strong> <span className="hint" data-testid="build-refresh-counts">{summary.done} done, {summary.failed} failed, {summary.skipped} skipped, {summary.notRun} not run</span></div>
          : <Problems answer={answer} />}
        {data && summary && answer.exit !== 0 && !data.dry_run && !data.results && <Problems answer={answer} />}
        {(data?.warnings ?? []).map((w, i) => <div key={i} className="hint" style={wrap} data-testid="build-refresh-warning">Warning: {w}</div>)}
        {(data?.results ?? []).length > 0 && (
          <div className="card flush" style={{ margin: '10px 0' }} data-testid="build-refresh-results">
            <div className="grid-head" style={{ gridTemplateColumns: '2fr 1fr 1fr 1fr 2fr', gap: 14 }}><span>Load</span><span>State</span><span>Rows</span><span>Time</span><span>Why</span></div>
            {data!.results!.map((r) => (
              <div key={`${r.model}.${r.member}`} className="grid-row auto" style={{ gridTemplateColumns: '2fr 1fr 1fr 1fr 2fr', gap: 14 }} data-testid={`build-refresh-load-${r.model}.${r.member}`}>
                <span className="mono">{r.model}.{r.member}</span><span data-testid="build-refresh-load-state">{r.state}</span><span>{r.rows ?? '-'}</span><span>{r.duration_ms === null || r.duration_ms === undefined ? '-' : `${r.duration_ms} ms`}</span><span style={wrap}>{r.reason ?? r.detail ?? ''}</span>
              </div>
            ))}
          </div>
        )}
        {data?.dry_run && (data.resolved_inputs ?? []).length > 0 && (
          <div style={{ margin: '10px 0' }} data-testid="build-refresh-resolved">
            <div className="hint">The inputs each load would run with:</div>
            {data.resolved_inputs!.map((l) => (
              <div key={`${l.model}.${l.member}`} className="hint" style={wrap}><span className="mono">{l.model}.{l.member}</span>: {l.inputs.length === 0 ? 'none' : l.inputs.map((i) => `${i.name} = ${i.value ?? 'null'} (${i.source})`).join(', ')}</div>
            ))}
          </div>
        )}
        {checks && <ChecksView checks={checks} />}
        {data && !data.dry_run && !checks && summary && <div className="hint" data-testid="build-refresh-no-checks">No checks were evaluated: this server records none, or no model that loaded has any.</div>}
      </div>
    </div>
  )
}

function ChecksView({ checks }: { checks: NonNullable<RefreshResult['checks']> }) {
  return (
    <div style={{ marginTop: 10 }} data-testid="build-refresh-checks">
      <strong>Checks of the models that loaded</strong>
      <div className="hint" data-testid="build-refresh-checks-counts">{checks.evaluated} evaluated: {checks.passed} passed, {checks.failed} failed, {checks.errors} could not run{checks.blocking > 0 ? `, ${checks.blocking} block their dependents` : ''}. Recorded under run {checks.run_id}.</div>
      {checks.error && <div className="banner error" role="alert" data-testid="build-refresh-checks-error"><span className="mark">!</span><span>The checks could not be recorded: {checks.error}</span></div>}
      {checks.notes.map((n, i) => <div key={i} className="hint" style={wrap} data-testid="build-refresh-checks-note">{n}</div>)}
      {checks.results.length > 0 && (
        <div className="card flush" style={{ marginTop: 8 }}>
          <div className="grid-head" style={{ gridTemplateColumns: '1.6fr 1.4fr 0.7fr 0.8fr 1fr 2.4fr', gap: 14 }}><span>Model</span><span>Check</span><span>Status</span><span>Severity</span><span>Measured / allowed</span><span>Detail</span></div>
          {checks.results.map((r) => (
            <div key={`${r.model}/${r.check}`} className="grid-row auto" style={{ gridTemplateColumns: '1.6fr 1.4fr 0.7fr 0.8fr 1fr 2.4fr', gap: 14 }} data-testid={`build-refresh-check-${r.model}-${r.check}`}>
              <span className="mono">{r.model}</span><span><span className="mono">{r.check}</span><div className="hint">{r.kind}</div></span>
              <span data-testid="build-refresh-check-status">{r.status}{r.blocking ? ' (blocks)' : ''}</span><span>{r.severity}</span><span>{measure(r.measured)} / {measure(r.threshold)}</span>
              <span style={wrap}>{r.message ?? ''}{r.sample_keys.length > 0 && <div className="hint">for example {r.sample_keys.slice(0, 3).join(' | ')}</div>}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

export interface RefreshViewProps {
  plans: RefreshPlanEntry[] | undefined
  loading: boolean
  listError: unknown
  /** Whether the server offers this person `connection refresh`; undefined until it said. */
  allowed: boolean | undefined
  choice: RefreshChoice
  dry: { key: string; ok: boolean } | null
  busy: 'dry' | 'run' | null
  answer: CommandAnswer<RefreshResult> | undefined
  failure: unknown
  onChoice: (patch: Partial<RefreshChoice>) => void
  onDryRun: () => void
  onRun: () => void
}

/** The screen without its calls. */
export function RefreshView(p: RefreshViewProps) {
  const connections = [...new Set((p.plans ?? []).map((x) => x.connection))].sort()
  const chosenPlan = (p.plans ?? []).find((x) => x.connection === p.choice.connection && x.name === p.choice.plan)
  const dryWhy = dryRunBlock(p.choice, p.allowed, p.busy !== null)
  const runWhy = runBlock(p.choice, p.allowed, p.busy !== null, p.dry)
  return (
    <div className="card" style={{ marginBottom: 14 }} data-testid="build-refresh">
      <div className="card-body">
        <strong>Refresh plans</strong>
        <div className="hint" style={{ margin: '4px 0 10px' }}>Runs the loads of a refresh plan on its connection, all of it or the members you choose. Try it first with a dry run: it checks everything and executes nothing. A run changes data on the target, needs the operator role, and is recorded under your name. When it ends the checks of the models that loaded are evaluated.</div>
        <ErrorBanner error={p.listError} />
        {p.loading && <div className="empty">Loading…</div>}
        {!p.loading && !p.listError && (p.plans ?? []).length === 0 && <div className="empty" data-testid="build-refresh-none">This project has no refresh plans. Compile it after giving a model a load and a <span className="mono">refresh</span> entry.</div>}
        {(p.plans ?? []).length > 0 && (
          <>
            <div className="row" style={{ gap: 12, flexWrap: 'wrap', alignItems: 'end' }}>
              <label className="stack" style={{ gap: 4 }}><span className="hint">Plan</span>
                <select className="input" value={p.choice.plan === '' ? '' : `${p.choice.connection}/${p.choice.plan}`} onChange={(e) => { const [c, ...n] = e.target.value.split('/'); p.onChoice({ connection: c ?? '', plan: n.join('/') }) }} data-testid="build-refresh-plan">
                  <option value="">Choose…</option>
                  {connections.flatMap((c) => plansOf(p.plans!, c).map((x) => <option key={`${c}/${x.name}`} value={`${c}/${x.name}`}>{c} / {x.name} ({x.members} loads)</option>))}
                </select>
              </label>
              <label className="stack" style={{ gap: 4, flex: 1, minWidth: 220 }}><span className="hint">Members (blank runs the whole plan)</span>
                <input className="input mono" value={p.choice.selectors} placeholder="marts.fct_events  tag:daily  +staging.events" onChange={(e) => p.onChoice({ selectors: e.target.value })} data-testid="build-refresh-selectors" />
              </label>
              <label className="row" style={{ gap: 6 }}><input type="checkbox" checked={p.choice.onlyFailed} onChange={(e) => p.onChoice({ onlyFailed: e.target.checked })} data-testid="build-refresh-only-failed" /> Only what failed last time</label>
            </div>
            {chosenPlan && <div className="hint" style={{ marginTop: 6, ...wrap }} data-testid="build-refresh-plan-about">{chosenPlan.title ?? chosenPlan.name}: {chosenPlan.members} loads{chosenPlan.settings.schedule ? `, scheduled ${chosenPlan.settings.schedule}` : ', not scheduled'}{chosenPlan.settings.parallel ? `, ${chosenPlan.settings.parallel} at a time` : ''}.</div>}
            <label className="stack" style={{ gap: 4, marginTop: 10 }}><span className="hint">Inputs, one <span className="mono">name=value</span> per line (replaces the load's default)</span>
              <textarea className="input mono" rows={2} value={p.choice.inputs} onChange={(e) => p.onChoice({ inputs: e.target.value })} data-testid="build-refresh-inputs" />
            </label>
            <div className="row" style={{ gap: 12, marginTop: 10 }}>
              <button type="button" className="btn" disabled={dryWhy !== null} title={dryWhy ?? undefined} onClick={p.onDryRun} data-testid="build-refresh-dry-run">{p.busy === 'dry' ? 'Checking…' : 'Dry run'}</button>
              <button type="button" className="btn btn-primary" disabled={runWhy !== null} title={runWhy ?? undefined} onClick={p.onRun} data-testid="build-refresh-run">{p.busy === 'run' ? 'Running…' : 'Run the refresh'}</button>
            </div>
            {(dryWhy ?? runWhy) && <div className="hint" style={{ marginTop: 6, ...wrap }} data-testid="build-refresh-why">{dryWhy ?? runWhy}</div>}
          </>
        )}
        {p.failure != null && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="build-refresh-failed"><span className="mark">!</span><span>{refreshFailure(p.failure)}</span></div>}
        <RefreshAnswerView answer={p.answer} running={p.busy !== null} />
      </div>
    </div>
  )
}

/** Running a refresh plan from the Plans tab: the plans from the compiled project, then a dry run, then the run. */
export function RefreshPanel({ project }: { project: string }) {
  const queryClient = useQueryClient()
  const [choice, setChoice] = useState<RefreshChoice>({ connection: '', plan: '', selectors: '', onlyFailed: false, inputs: '', dryRun: false })
  const [dry, setDry] = useState<{ key: string; ok: boolean } | null>(null)
  const plans = useQuery({ queryKey: ['build', project, 'refresh-plans'], queryFn: () => buildApi.refreshPlans(project) })
  const commands = useQuery({ queryKey: ['build', project, 'commands'], queryFn: () => buildApi.commands(project) })
  const allowed = commands.data?.find((c) => c.tool === 'connection_refresh')?.allowed
  const mutation = useMutation({
    mutationFn: (dryRun: boolean) => buildApi.refresh(project, refreshBody({ ...choice, dryRun })),
    onSuccess: (answer, dryRun) => {
      if (dryRun) setDry({ key: choiceKey(choice), ok: answer.exit === 0 })
      else void queryClient.invalidateQueries({ queryKey: ['build', project, 'status'] })
    },
  })
  const busy = mutation.isPending ? (mutation.variables ? 'dry' : 'run') : null
  return (
    <RefreshView
      plans={plans.data} loading={plans.isLoading} listError={plans.error} allowed={allowed}
      choice={choice} dry={dry} busy={busy} answer={mutation.data} failure={mutation.error}
      onChoice={(patch) => setChoice((c) => ({ ...c, ...patch }))}
      onDryRun={() => mutation.mutate(true)} onRun={() => mutation.mutate(false)}
    />
  )
}
