import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type CheckDiagnostic, type ProvisioningPlan, type ProvisioningSide, type ToolAnswer } from '../api'
import { replicationsOf } from '../syncSchemas'

const findingLine = (d: CheckDiagnostic) => `${d.file ? `${d.file}${d.line ? `:${d.line}` : ''} ` : ''}${d.code}: ${d.message}`

/** What each state says in a sentence; `unknown` is never shown as if nothing were needed. */
export const stateWord = (state: ProvisioningSide['state']): string =>
  state === 'satisfied' ? 'nothing to do' : state === 'missing' ? 'would change' : state === 'unsupported' ? 'cannot be planned' : 'not known from the cache'

function Side({ label, side }: { label: string; side: ProvisioningSide | null }) {
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
    </div>
  )
}

/** The plan of a replication's tables: per mapping the source and the target side, each with its statements read as the server would run them. Read only: it never offers to run them. */
export function ProvisioningPlanView({ answer }: { answer: ToolAnswer<ProvisioningPlan> }) {
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
              <Side label="source" side={m.source_plan} />
              <Side label="target" side={m.target_plan} />
            </div>
          ))}
        </>
      )}
    </div>
  )
}

/**
 * The Provisioning tab of a Sync project's workspace (plan 33 SC22j): what a replication needs of its two databases, planned offline from the draft's catalog cache (the target's tables and columns) and the mappings' own column caches. The statements are the product's planners'
 * (the create or alter of the target table). The cache holds no index, foreign key or default, and the source's change-capture state is the live server's, so those say so; **apply** runs DDL and is the Sync server's, never offered here.
 */
export function SyncProvisioningPanel({ project, id, paths }: { project: string; id: string; paths: readonly string[] }) {
  const replications = replicationsOf(paths)
  const [chosen, setChosen] = useState('')
  const replication = chosen !== '' && replications.includes(chosen) ? chosen : (replications[0] ?? '')
  const plan = useMutation({ mutationFn: (name: string) => workspaceApi.provisioningPlan(project, id, name) })
  return (
    <div data-testid="wsp-prov">
      <div className="hint" style={{ marginBottom: 6 }}>The plan of a replication's tables, made offline from the catalog cache of this draft. Applying it runs DDL and is the Sync server's.</div>
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
      {plan.data && <ProvisioningPlanView answer={plan.data} />}
    </div>
  )
}
