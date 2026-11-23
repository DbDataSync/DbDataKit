import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type CheckDiagnostic, type ProvisioningPlan, type ProvisioningSide, type StagedRun, type ToolAnswer } from '../api'
import { deployApi, useRunnerVersion } from '../deploys'
import { replicationsOf } from '../syncSchemas'

/** The step the Sync task `provision` records its report as. */
export const AppliedStep = 'provision-applied'

/**
 * Reads a run back until it ended (each `intervalMs`, at most `attempts` times); the last read when it has not ended by then. A run the Runner has queued but not started is not on its record yet: a read answered 404 is tried
 * again until the run is seen, `queuedAttempts` times at most.
 */
export async function followStagedRun(read: () => Promise<StagedRun>, intervalMs = 1000, attempts = 600, queuedAttempts = 60): Promise<StagedRun> {
  let run: StagedRun | null = null
  for (let i = 0; run === null; i++) {
    try { run = await read() }
    catch (e) {
      if (!(e instanceof ApiError && e.status === 404) || i + 1 >= queuedAttempts) throw e
      await new Promise((r) => setTimeout(r, intervalMs))
    }
  }
  for (let i = 1; i < attempts && !run.finished; i++) {
    await new Promise((r) => setTimeout(r, intervalMs))
    run = await read()
  }
  return run
}

interface ReportStep { title?: string; succeeded?: boolean; error?: string | null }
/** The statements a run of `provision` reports, each with whether it ran (the run's `provision-applied` step). */
export const reportedSteps = (run: StagedRun): ReportStep[] =>
  (run.steps ?? []).flatMap((s) => ((s.result as { steps?: ReportStep[] } | null)?.steps ?? []))

function RunResult({ run }: { run: StagedRun }) {
  return (
    <div className="card flush" style={{ marginTop: 8, padding: '6px 10px' }} data-testid="wsp-prov-run-result" data-outcome={run.outcome}>
      <div>Run <span className="mono">{run.id}</span> of version {run.version ?? '?'}: <span className={run.outcome === 'succeeded' ? '' : 'error'}>{run.finished ? run.outcome : `${run.outcome}, not ended yet; follow it on the Runner`}</span></div>
      {run.failure && <div className="error" data-testid="wsp-prov-run-failure">{run.failure.message}</div>}
      {reportedSteps(run).map((s, i) => <div key={i} className={s.succeeded ? 'hint' : 'error'} data-testid="wsp-prov-run-step">{s.succeeded ? 'Done' : 'Failed'}: {s.title}{s.error ? ` (${s.error})` : ''}</div>)}
    </div>
  )
}

const findingLine = (d: CheckDiagnostic) => `${d.file ? `${d.file}${d.line ? `:${d.line}` : ''} ` : ''}${d.code}: ${d.message}`

/** What each state says in a sentence; `unknown` is never shown as if nothing were needed. */
export const stateWord = (state: ProvisioningSide['state']): string =>
  state === 'satisfied' ? 'nothing to do' : state === 'missing' ? 'would change' : state === 'unsupported' ? 'cannot be planned' : 'not known from the cache'

type Apply = (mapping: string, action: string) => void

function Side({ label, side, mapping, onApply }: { label: string; side: ProvisioningSide | null; mapping: string; onApply?: Apply }) {
  if (!side) return null
  return (
    <div style={{ marginTop: 6 }} data-testid="wsp-prov-side" data-side={label} data-state={side.state}>
      <div><span className="hint">{label}</span> <span className="mono">{side.action}</span>: <span className={side.state === 'missing' ? '' : 'hint'} data-testid="wsp-prov-state">{stateWord(side.state)}</span></div>
      {side.steps.map((s, i) => (
        <div key={i} data-testid="wsp-prov-step">
          <div className="hint">{s.title}{s.rationale ? ` (${s.rationale})` : ''}</div>
          <pre className="mono" style={{ margin: 0, overflow: 'auto', maxHeight: 220 }} data-testid="wsp-prov-sql">{s.command_text}</pre>
        </div>
      ))}
      {side.warnings.map((w, i) => <div key={i} className="hint" data-testid="wsp-prov-warning">{w}</div>)}
      {onApply && side.state !== 'satisfied' && side.state !== 'unsupported' && (
        <button type="button" className="btn" style={{ marginTop: 4 }} data-testid="wsp-prov-apply" onClick={() => onApply(mapping, side.action)}>Apply on the Runner</button>
      )}
    </div>
  )
}

/** The plan of a replication's tables: per mapping the source and the target side, each with its statements read as the server would run them. With `onApply`, a side that would change offers to apply it on the Runner. */
export function ProvisioningPlanView({ answer, onApply }: { answer: ToolAnswer<ProvisioningPlan>; onApply?: Apply }) {
  const plan = answer.data
  return (
    <div data-testid="wsp-prov-result" data-ok={String(answer.ok)}>
      {answer.diagnostics.map((d, i) => <div key={i} className="error" data-testid="wsp-prov-finding">{findingLine(d)}</div>)}
      {plan && (
        <>
          <div className="hint" data-testid="wsp-prov-cache">From the catalog cache made {plan.cache_generated_at ?? 'at an unknown time'}{plan.cache_server ? ` by ${plan.cache_server}` : ''}: no connection was opened.</div>
          {plan.mappings.length === 0 && <div className="hint" data-testid="wsp-prov-none">The replication has no table mapping.</div>}
          {plan.mappings.map((m) => (
            <div key={m.mapping} className="card flush" style={{ marginTop: 8, padding: '6px 10px' }} data-testid="wsp-prov-mapping" data-name={m.mapping}>
              <div className="mono">{m.mapping}{m.source && m.target ? <span className="hint"> {m.source.connection}.{m.source.table} to {m.target.connection}.{m.target.table}</span> : null}</div>
              {m.excluded && <div className="error" data-testid="wsp-prov-excluded">Not planned: {m.excluded}</div>}
              <Side label="source" side={m.source_plan} mapping={m.mapping} onApply={onApply} />
              <Side label="target" side={m.target_plan} mapping={m.mapping} onApply={onApply} />
            </div>
          ))}
        </>
      )}
    </div>
  )
}

/**
 * The Provisioning tab of a Sync project's workspace (plan 33 SC22j): what a replication needs of its two databases, planned offline from the draft's catalog cache (the target's tables and columns) and the mappings' own column caches. The statements are the product's planners'
 * (the create or alter of the target table). The cache holds no index, foreign key or default, and the source's change-capture state is the live server's, so those say so. **Apply** (plan 28c E21, step e) runs the set's manual task `provision`
 * of the version the console's newest deploy of a build of this draft put on a Runner, for the signed-in person, through the console (Design: docs/publishing.md#runs-of-a-deployed-build): the DDL runs there under the logins bound on the
 * Runner's channel, and the Workspace server holds none and calls no Runner.
 */
export function SyncProvisioningPanel({ project, id, paths }: { project: string; id: string; paths: readonly string[] }) {
  const replications = replicationsOf(paths)
  const [chosen, setChosen] = useState('')
  const replication = chosen !== '' && replications.includes(chosen) ? chosen : (replications[0] ?? '')
  const plan = useMutation({ mutationFn: (name: string) => workspaceApi.provisioningPlan(project, id, name) })
  // the version the console's newest deploy of a build of this draft put on a Runner
  const staged = useRunnerVersion(project, id)
  const apply = useMutation({
    mutationFn: async ({ mapping, action }: { mapping: string; action: string }) => {
      const at = staged.data
      if (!at) throw new Error('No build of this draft is staged on a Runner. Build it and publish it to a Runner (staged, not served); then apply.')
      const started = await deployApi.run(at.deploy, 'provision', { replication, mapping, action, steps: '', repo_dir: 'repo' })
      return followStagedRun(() => deployApi.runOf(at.deploy, started.run.run_id, AppliedStep))
    },
  })
  return (
    <div data-testid="wsp-prov">
      <div className="hint" style={{ marginBottom: 6 }}>The plan of a replication's tables, made offline from the catalog cache of this draft. Applying it runs DDL on a Runner, as the task provision of the version a build of this draft staged there.</div>
      <div className="hint" data-testid="wsp-prov-staged">{staged.data ? `${staged.data.mode === 'serve' ? 'Published' : 'Staged'}: ${staged.data.package} version ${staged.data.version} on ${staged.data.channel} of ${staged.data.targetName}.` : staged.isLoading ? '' : 'No build of this draft is staged on a Runner yet, so nothing can be applied.'}</div>
      {replications.length === 0
        ? <div className="hint" data-testid="wsp-prov-no-replication">No replication yet.</div>
        : (
          <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <select className="input mono" aria-label="Replication" value={replication} onChange={(e) => setChosen(e.target.value)} data-testid="wsp-prov-replication">
              {replications.map((r) => <option key={r} value={r}>{r}</option>)}
            </select>
            <button type="button" className="btn btn-primary" disabled={plan.isPending} data-testid="wsp-prov-run" onClick={() => plan.mutate(replication)}>{plan.isPending ? 'Planning...' : 'Plan from the catalog cache'}</button>
          </div>
        )}
      <ErrorBanner error={plan.error} />
      {plan.data && <ProvisioningPlanView answer={plan.data} onApply={staged.data && !apply.isPending ? (mapping, action) => apply.mutate({ mapping, action }) : undefined} />}
      {apply.isPending && <div className="hint" data-testid="wsp-prov-applying">Applying on the Runner...</div>}
      <ErrorBanner error={apply.error} />
      {apply.data && <RunResult run={apply.data} />}
    </div>
  )
}
