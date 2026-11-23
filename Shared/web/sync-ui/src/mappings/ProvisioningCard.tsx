import { InheritableToggle } from '@dbdatakit/shell'
import type { ProvisioningMapping, ProvisioningSide } from '@dbdatakit/workspace'
import type { ProvisioningConfig } from '../types'

const DOT: Record<ProvisioningSide['state'], string> = { satisfied: 'dot-ok', missing: 'dot-warn', unsupported: 'dot-bad', unknown: 'dot-idle' }

/** What a side's state says in words; `unknown` is never shown as if nothing were needed. */
export const stateWords = (state: ProvisioningSide['state']): string =>
  state === 'satisfied' ? 'nothing to do' : state === 'missing' ? 'would change' : state === 'unsupported' ? 'cannot be planned' : 'not known from the cache'

/** How many statements the mapping's plan would run on its two sides: the count the mapping's Provisioning tab shows. */
export const pendingSteps = (plan: ProvisioningMapping | null | undefined): number =>
  [plan?.source_plan, plan?.target_plan].reduce((n, side) => n + (side?.state === 'missing' ? side.steps.length : 0), 0)

function PlanPanel({ label, side, testId, plan }: { label: string; side: 'source' | 'target'; testId: string; plan: ProvisioningSide }) {
  return (
    <div className={`card side-${side}`} data-testid={testId} data-side={side} data-state={plan.state}>
      <div className="card-head">
        <span className={`card-title side-${side}`}>{label}</span>
        <span className="status"><span className={`dot ${DOT[plan.state]}`} />{stateWords(plan.state)}</span>
        <span className="card-note mono">{plan.action}</span>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {plan.warnings.length > 0 && <div className="banner warn" role="alert" data-testid={`${testId}-warnings`}>{plan.warnings.map((w, i) => <div key={i}>{w}</div>)}</div>}
        {plan.steps.map((s, i) => (
          <div key={i} data-testid={`${testId}-step`}>
            <div className="hint">{s.title}{s.rationale ? ` (${s.rationale})` : ''}</div>
            <pre className="mono" style={{ margin: 0, overflow: 'auto', maxWidth: '100%', maxHeight: 220 }} data-testid={`${testId}-sql`}>{s.command_text}</pre>
          </div>
        ))}
        {plan.steps.length === 0 && plan.state === 'satisfied' && <span className="hint">Nothing to do.</span>}
      </div>
    </div>
  )
}

/**
 * A mapping's provisioning: what it may do to its target (each setting the replication's unless overridden here), and below it what that comes to
 * on the two sides, from the replication's plan made offline from the draft's catalog cache. The statements are applied from the replication's
 * Provisioning tab, never from here.
 */
export function ProvisioningCard({ provisioning, inherited, onChangeProvisioning, plan, editable = true }: {
  provisioning: ProvisioningConfig
  /** The replication's answer, shown while this mapping takes it. */
  inherited: ProvisioningConfig | null | undefined
  onChangeProvisioning: (value: ProvisioningConfig) => void
  /** This mapping's entry of the replication's plan; undefined while none was made. */
  plan?: ProvisioningMapping
  editable?: boolean
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }} data-testid="mapping-provisioning">
      <fieldset className="card" style={{ border: 0, margin: 0, padding: 0 }} disabled={!editable}>
        <div className="card-head">
          <span className="card-title">Provisioning</span>
          <span className="card-note">the replication's unless overridden here</span>
        </div>
        <div className="card-body" style={{ gap: 14 }}>
          <InheritableToggle label="Create target table if missing" description="Creates the table only when it does not exist. Never alters one that does."
            value={provisioning.createTargetTableIfMissing} inherited={inherited?.createTargetTableIfMissing ?? false}
            onChange={(next) => onChangeProvisioning({ ...provisioning, createTargetTableIfMissing: next })} testId="provisioning-create" />
          <div className="divider" />
          <InheritableToggle label="Alter target columns if missing or changed" description="Adds a mapped column the target lacks and changes one whose type no longer matches. Never drops a column."
            value={provisioning.alterTargetTableColumnsIfMissingOrChanged} inherited={inherited?.alterTargetTableColumnsIfMissingOrChanged ?? false}
            onChange={(next) => onChangeProvisioning({ ...provisioning, alterTargetTableColumnsIfMissingOrChanged: next })} testId="provisioning-alter" />
        </div>
      </fieldset>

      {plan?.excluded && <div className="banner warn" data-testid="provisioning-excluded">Not planned: {plan.excluded}</div>}
      {plan && !plan.excluded && (
        <div className="form-grid">
          {plan.source_plan && <PlanPanel label="Source" side="source" testId="provisioning-plan-source" plan={plan.source_plan} />}
          {plan.target_plan && <PlanPanel label="Target" side="target" testId="provisioning-plan-target" plan={plan.target_plan} />}
        </div>
      )}
    </div>
  )
}
