import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useOutletContext } from 'react-router-dom'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { CommandAnswer, Diagnostic } from '../types'

interface ProjectContext {
  project: string
}

const useProject = () => useOutletContext<ProjectContext>()

/** Why a command that did not run to the end says so: its diagnostics, its errors, or what it printed. */
function Problems({ answer }: { answer: CommandAnswer<unknown> | undefined }) {
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

/** The connection a screen speaks about. Left empty it is the project's default connection, which is what the commands themselves do. */
function ConnectionField({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  return (
    <label className="row" style={{ gap: 8 }}>
      <span className="hint">Connection</span>
      <input className="input mono" value={value} placeholder="the project's default" onChange={(e) => onChange(e.target.value)} data-testid="build-connection" style={{ width: 200 }} />
    </label>
  )
}

/** Where a connection stands: every object the models declare and whether it exists, what a deploy would do, and the refresh plans. Reads the target and writes nothing. */
export function StatusTab() {
  const { project } = useProject()
  const [connection, setConnection] = useState('')
  const [asked, setAsked] = useState('')
  const status = useQuery({ queryKey: ['build', project, 'status', asked], queryFn: () => buildApi.status(project, asked || undefined) })
  const answer = status.data
  const data = answer?.document?.data
  return (
    <div data-testid="build-status">
      <form className="row" style={{ gap: 12, marginBottom: 14 }} onSubmit={(e) => { e.preventDefault(); setAsked(connection); void status.refetch() }}>
        <ConnectionField value={connection} onChange={setConnection} />
        <button type="submit" className="btn" disabled={status.isFetching} data-testid="build-status-refresh">{status.isFetching ? 'Reading…' : 'Read the status'}</button>
      </form>
      <ErrorBanner error={status.error} />
      <Problems answer={answer} />
      {data && (
        <>
          <div className="card" style={{ marginBottom: 14 }}>
            <div className="card-body" data-testid="build-status-summary">
              <strong>{data.connection}</strong>{data.summary && <> · structure <span data-testid="build-status-structure">{data.summary.structure}</span> · {data.summary.steps} steps, {data.summary.loads} loads · {data.summary.risky} risky, {data.summary.destructive} destructive</>}
              {data.summary && data.summary.changed_outside_the_tool > 0 && <div className="hint" data-testid="build-status-drift">{data.summary.changed_outside_the_tool} object(s) changed outside the tool (drift).</div>}
              {data.summary && <div className="hint">{data.summary.policy}</div>}
            </div>
          </div>
          <div className="card flush" style={{ marginBottom: 14 }} data-testid="build-status-objects">
            <div className="grid-head" style={{ gridTemplateColumns: '3fr 1fr', gap: 14 }}><span>Object</span><span>State</span></div>
            {data.objects.map((o) => (
              <div key={o.name} className="grid-row" style={{ gridTemplateColumns: '3fr 1fr', gap: 14 }} data-testid={`build-status-object-${o.name}`}>
                <span className="mono">{o.name}</span><span data-testid="build-status-state">{o.state}</span>
              </div>
            ))}
          </div>
          {(data.plans ?? []).length > 0 && (
            <div className="card flush" data-testid="build-status-refresh-plans">
              <div className="grid-head" style={{ gridTemplateColumns: '2fr 2fr 1fr', gap: 14 }}><span>Refresh plan</span><span>Last run</span><span>Health</span></div>
              {(data.plans ?? []).map((p) => (
                <div key={p.plan} className="grid-row" style={{ gridTemplateColumns: '2fr 2fr 1fr', gap: 14 }}>
                  <span className="mono">{p.plan}</span><span>{p.outcome}</span><span>{p.health?.overdue ? 'overdue' : p.health?.slow ? 'slow' : 'ok'}</span>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  )
}

/**
 * Deploy plans: the plan files of the project, one read in full, and the steps that turn it into the target. A plan is written, reviewed, tried (a dry run runs every check and executes nothing), and applied.
 * Applying needs the approver role on the server; whoever applies is the approver and the server records them. Risky and destructive steps are not applied unless the person allows them here.
 */
export function PlansTab() {
  const { project } = useProject()
  const queryClient = useQueryClient()
  const [connection, setConnection] = useState('')
  const [chosen, setChosen] = useState<string | null>(null)
  const plans = useQuery({ queryKey: ['build', project, 'plans'], queryFn: () => buildApi.plans(project) })
  const write = useMutation({
    mutationFn: () => buildApi.writePlan(project, connection || undefined),
    onSuccess: async (answer) => {
      await queryClient.invalidateQueries({ queryKey: ['build', project, 'plans'] })
      const id = answer.document?.data?.plan?.id
      const written = id ? (await buildApi.plans(project)).document?.data.plans.find((p) => p.id === id) : undefined
      if (written) setChosen(written.path)
    },
  })
  const list = plans.data?.document?.data?.plans ?? []
  return (
    <div data-testid="build-plans">
      <div className="row" style={{ gap: 12, marginBottom: 14 }}>
        <ConnectionField value={connection} onChange={setConnection} />
        <button type="button" className="btn btn-primary" disabled={write.isPending} onClick={() => write.mutate()} data-testid="build-plan-write">{write.isPending ? 'Planning…' : 'Plan a deploy'}</button>
        <span className="hint">Reads the target, writes the plan file, applies nothing.</span>
      </div>
      <ErrorBanner error={plans.error ?? write.error} />
      <Problems answer={write.data} />
      <div className="card flush" style={{ marginBottom: 14 }}>
        <div className="grid-head" style={{ gridTemplateColumns: '2fr 1fr 0.7fr 0.7fr 0.7fr', gap: 14 }}><span>Plan</span><span>Connection</span><span>Steps</span><span>Risky</span><span>Destructive</span></div>
        {plans.data && list.length === 0 && <div className="empty" data-testid="build-plans-empty">No plans yet. Plan a deploy to write one.</div>}
        {list.map((p) => (
          <button key={p.path} type="button" className="grid-row" style={{ gridTemplateColumns: '2fr 1fr 0.7fr 0.7fr 0.7fr', gap: 14, textAlign: 'left', width: '100%', background: p.path === chosen ? 'var(--surface-2, transparent)' : 'transparent' }} onClick={() => setChosen(p.path === chosen ? null : p.path)} data-testid={`build-plan-${p.id}`}>
            <span className="mono">{p.id}{!p.intact && ' (changed since written)'}</span><span>{p.connection}</span><span>{p.steps}</span><span>{p.risky}</span><span>{p.destructive}</span>
          </button>
        ))}
      </div>
      {chosen && <PlanReview project={project} path={chosen} connection={connection} />}
    </div>
  )
}

function PlanReview({ project, path, connection }: { project: string; path: string; connection: string }) {
  const queryClient = useQueryClient()
  const detail = useQuery({ queryKey: ['build', project, 'plan', path], queryFn: () => buildApi.plan(project, path) })
  const [allowRisky, setAllowRisky] = useState(false)
  const [allowed, setAllowed] = useState<string[]>([])
  const dry = useMutation({ mutationFn: () => buildApi.dryRun(project, path, connection || undefined) })
  const apply = useMutation({
    mutationFn: () => buildApi.apply(project, path, { risky: allowRisky, destructive: allowed }, connection || undefined),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['build', project] }),
  })
  const document = detail.data?.document
  const plan = document?.data
  const needs = plan?.apply_needs
  const result = apply.data?.document?.data
  return (
    <div className="card" data-testid="build-plan-review">
      <div className="card-body">
        <ErrorBanner error={detail.error} />
        <Problems answer={detail.data} />
        {plan && (
          <>
            <h3 style={{ marginTop: 0 }}><span className="mono">{plan.plan.id}</span></h3>
            <div className="hint" data-testid="build-plan-facts">{plan.plan.steps.length} steps · {plan.risky} risky · {plan.destructive} destructive · built from {plan.plan.git_commit.slice(0, 8)}{plan.plan.git_dirty ? ' (working tree was dirty)' : ''}{!plan.intact && ' · the file changed since it was written'}</div>
            <div className="card flush" style={{ margin: '12px 0' }} data-testid="build-plan-steps">
              <div className="grid-head" style={{ gridTemplateColumns: '0.4fr 1.2fr 3fr 0.8fr', gap: 14 }}><span>#</span><span>Object</span><span>What</span><span>Risk</span></div>
              {plan.plan.steps.map((s) => (
                <details key={s.id} className="grid-row" style={{ display: 'block' }} data-testid={`build-plan-step-${s.id}`}>
                  <summary style={{ display: 'grid', gridTemplateColumns: '0.4fr 1.2fr 3fr 0.8fr', gap: 14, cursor: 'pointer' }}>
                    <span>{s.id}</span><span className="mono">{s.object}</span><span>{s.description}</span><span data-risk={s.risk}>{s.risk}</span>
                  </summary>
                  <pre className="mono" style={{ whiteSpace: 'pre-wrap', margin: '8px 0 0' }}>{s.text}</pre>
                </details>
              ))}
            </div>
            {needs && (needs.allow_risky || needs.allow_destructive.length > 0) && (
              <div style={{ marginBottom: 12 }} data-testid="build-plan-allowances">
                <div className="hint">This plan has steps that are not applied unless you allow them.</div>
                {needs.allow_risky && <label className="row" style={{ gap: 8 }}><input type="checkbox" checked={allowRisky} onChange={(e) => setAllowRisky(e.target.checked)} data-testid="build-allow-risky" /> Allow the risky steps</label>}
                {needs.allow_destructive.map((o) => (
                  <label key={o} className="row" style={{ gap: 8 }}>
                    <input type="checkbox" checked={allowed.includes(o)} onChange={(e) => setAllowed(e.target.checked ? [...allowed, o] : allowed.filter((x) => x !== o))} data-testid={`build-allow-destructive-${o}`} /> Allow the destructive steps on <span className="mono">{o}</span>
                  </label>
                ))}
              </div>
            )}
            <div className="row" style={{ gap: 12 }}>
              <button type="button" className="btn" disabled={dry.isPending || apply.isPending} onClick={() => dry.mutate()} data-testid="build-plan-dry-run">{dry.isPending ? 'Checking…' : 'Dry run'}</button>
              <button type="button" className="btn btn-primary" disabled={apply.isPending || dry.isPending} onClick={() => apply.mutate()} data-testid="build-plan-apply">{apply.isPending ? 'Applying…' : 'Approve and apply'}</button>
              <span className="hint">Applying changes the target. It needs the approver role, and you are recorded as the approver.</span>
            </div>
            <ErrorBanner error={dry.error ?? apply.error} />
            <Problems answer={dry.data} />
            <Problems answer={apply.data} />
            {dry.data?.exit === 0 && <div className="hint" style={{ marginTop: 8 }} data-testid="build-plan-dry-ok">The dry run passed every check and executed nothing.</div>}
            {apply.data?.exit === 0 && <div style={{ marginTop: 8 }} data-testid="build-plan-applied">Applied{result?.plan_id ? <> <span className="mono">{result.plan_id}</span></> : null}.</div>}
          </>
        )}
      </div>
    </div>
  )
}

/** The models run on generated rows, offline: what each would produce, without a target. */
export function SampleTab() {
  const { project } = useProject()
  const sample = useMutation({ mutationFn: () => buildApi.sample(project) })
  const tables = sample.data?.document?.data?.tables ?? []
  return (
    <div data-testid="build-sample">
      <div className="row" style={{ gap: 12, marginBottom: 14 }}>
        <button type="button" className="btn btn-primary" disabled={sample.isPending} onClick={() => sample.mutate()} data-testid="build-sample-button">{sample.isPending ? 'Running…' : 'Run on sample rows'}</button>
        <span className="hint">Generates rows for what the project reads and runs every model on them. No database is touched.</span>
      </div>
      <ErrorBanner error={sample.error} />
      <Problems answer={sample.data} />
      {tables.map((t) => (
        <div key={t.name} className="card flush" style={{ marginBottom: 14, overflow: 'auto' }} data-testid={`build-sample-table-${t.name}`}>
          <div className="card-body"><strong className="mono">{t.name}</strong> <span className="hint">{t.kind} · {t.row_count} rows</span></div>
          <div className="grid-head" style={{ gridTemplateColumns: `repeat(${t.columns.length}, minmax(90px, 1fr))`, gap: 14 }}>{t.columns.map((c) => <span key={c.name}>{c.name}</span>)}</div>
          {t.rows.map((r, i) => (
            <div key={i} className="grid-row" style={{ gridTemplateColumns: `repeat(${t.columns.length}, minmax(90px, 1fr))`, gap: 14 }}>{r.map((v, j) => <span key={j} className="mono">{v ?? '∅'}</span>)}</div>
          ))}
        </div>
      ))}
    </div>
  )
}
