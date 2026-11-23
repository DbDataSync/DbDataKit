import { useMemo, type ReactNode } from 'react'
import type { ProvisioningPlan, ToolAnswer } from '@dbdatakit/workspace'
import { groupPlan, scriptFor, type ApplyTarget, type ProvisioningGroup } from './provisioningGroups'

/**
 * A replication's provisioning plan in the old Provisioning tab's layout: per connection and side, each statement once with the mappings that need
 * it, a **Copy** of the group's statements as a script, and the mappings the plan could not say anything about. The plan is the Workspace's, made
 * offline from the draft's catalog cache, so no connection was opened. With `onApply`, a mapping's side that would change offers **Apply**, which
 * runs the task `provision` on a Runner as the Files area's Provisioning tab does.
 */
export function ReplicationProvisioningPanel({ replication, answer, onApply, mappingLink }: {
  replication: string
  answer: ToolAnswer<ProvisioningPlan>
  onApply?: (target: ApplyTarget) => void
  /** Where a mapping's name leads (its Provisioning tab); plain text without it. */
  mappingLink?: (mapping: string) => ReactNode
}) {
  const plan = answer.data
  const grouped = useMemo(() => (plan ? groupPlan(plan) : null), [plan])
  const name = (m: string) => (mappingLink ? mappingLink(m) : <span className="mono">{m}</span>)

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }} data-testid="replication-provisioning-panel" data-ok={String(answer.ok)}>
      {answer.diagnostics.map((d, i) => (
        <div key={i} className="banner warn" role="alert" data-testid="provisioning-finding">{`${d.file ? `${d.file}${d.line ? `:${d.line}` : ''} ` : ''}${d.code}: ${d.message}`}</div>
      ))}
      {plan && grouped && (
        <>
          <span className="hint" data-testid="provisioning-from-cache">
            {`Planned from the catalog cache made ${plan.cache_generated_at ?? 'at an unknown time'}${plan.cache_server ? ` by ${plan.cache_server}` : ''}; no connection was opened.`}
          </span>
          {grouped.groups.length === 0 && grouped.excluded.length === 0 && <span className="hint" data-testid="provisioning-nothing">Nothing to provision.</span>}
          {grouped.groups.map((g) => <GroupPanel key={`${g.side}/${g.connection}`} replication={replication} group={g} onApply={onApply} name={name} />)}
          {grouped.excluded.length > 0 && (
            <div className="card" data-testid="provisioning-excluded">
              <div className="card-head">
                <span className="card-title">Not included</span>
                <span className="card-note">mappings this plan could not say anything about</span>
              </div>
              <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {grouped.excluded.map((x) => (
                  <div key={`${x.mapping}-${x.side ?? ''}`} data-testid="provisioning-excluded-row" data-mapping={x.mapping}>
                    <div className="row" style={{ gap: 8 }}>{name(x.mapping)}{x.side && <span className="badge">{x.side}</span>}</div>
                    <div className="hint">{x.reason}</div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </>
      )}
    </div>
  )
}

function GroupPanel({ replication, group, onApply, name }: {
  replication: string
  group: ProvisioningGroup
  onApply?: (target: ApplyTarget) => void
  name: (mapping: string) => ReactNode
}) {
  const copy = async () => {
    // the browser may refuse the clipboard; the statements are still there to select
    try { await navigator.clipboard.writeText(scriptFor(replication, group, new Date().toISOString())) } catch { /* nothing to do */ }
  }
  return (
    <div className={`card side-${group.side}`} data-testid="provisioning-group" data-connection={group.connection} data-side={group.side}>
      <div className="card-head">
        <span className={`card-title side-${group.side}`}>{group.connection}</span>
        <span className="status">{group.side}</span>
        <button type="button" className="btn spacer" onClick={() => void copy()} data-testid="provisioning-group-copy">Copy</button>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {group.warnings.length > 0 && <div className="banner warn" role="alert" data-testid="provisioning-group-warnings">{group.warnings.map((w, i) => <div key={i}>{w}</div>)}</div>}
        {group.steps.map((step, i) => (
          <div key={i} className="provisioning-step" data-testid="provisioning-step" data-step-title={step.title}>
            <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
              <span>{step.title}</span>
              <span className="badge" data-testid="provisioning-step-scope">{step.scope}</span>
            </div>
            {step.rationale && <div className="hint">{step.rationale}</div>}
            <pre className="mono" style={{ margin: 0, overflow: 'auto', maxWidth: '100%', maxHeight: 220 }} data-testid="provisioning-step-sql">{step.commandText}</pre>
            <div className="hint" data-testid="provisioning-step-mappings">
              {step.mappings.map((m, j) => <span key={m}>{j > 0 && ', '}{name(m)}</span>)}
            </div>
          </div>
        ))}
        {onApply && (
          <div className="row" style={{ gap: 8, flexWrap: 'wrap' }} data-testid="provisioning-group-apply">
            <span className="hint">Apply on a Runner:</span>
            {group.apply.map((a) => (
              <button key={a.mapping} type="button" className="btn btn-sm" onClick={() => onApply(a)} data-testid="provisioning-apply" data-mapping={a.mapping} data-action={a.action}>{a.mapping}</button>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
