import type { ReactNode } from 'react'
import { ErrorBanner, InheritableToggle } from '@dbdatakit/shell'
import { ProvisioningRunResult, useDraft, useProvisioningApply, type RunnerVersion } from '@dbdatakit/workspace'
import { useDraftProvisioningPlan } from '../api/hooks'
import { isQuestionPath } from '../api/syncApi'
import { NewestPreflight } from './NewestPreflight'
import { ReplicationProvisioningPanel } from './ReplicationProvisioningPanel'
import type { ProvisioningConfig, ReplicationTaskConfig } from '../types'

/** The replication's provisioning block, or one where neither setting has been said. */
const provisioningOf = (draft: ReplicationTaskConfig): ProvisioningConfig =>
  draft.provisioning ?? { createTargetTableIfMissing: null, alterTargetTableColumnsIfMissingOrChanged: null }

/** What the tab says about the version **Apply** runs on. */
export function stagedLine(staged: RunnerVersion | null | undefined, loading: boolean): string {
  if (staged) return `Apply runs the task provision of ${staged.package} version ${staged.version}, ${staged.mode === 'serve' ? 'published' : 'staged'} on ${staged.channel} of ${staged.targetName}.`
  return loading ? '' : 'No build of this draft is staged on a Runner yet, so nothing can be applied. Build and publish it to a Runner first.'
}

/**
 * The Overview's Provisioning tab: what every table mapping of the replication may do to its target unless the mapping says otherwise, and below it
 * what that amounts to: the replication's plan, made by the Workspace offline from the draft's files and catalog cache with the unsaved edits
 * over them, in the old tab's layout. **Apply** on a mapping's side runs the set's task `provision` of the version a build of this draft staged on a Runner, through the
 * console as the person, as the Files area's Provisioning tab does. Under the plan, the newest `preflight` of a channel the draft was published to says
 * what the live check found for each mapping.
 */
export function ProvisioningTab({ project, id, replication, draft, setDraft, mappingLink, editable = true }: {
  project: string
  id: string
  replication: string
  draft: ReplicationTaskConfig
  setDraft: (next: ReplicationTaskConfig) => void
  mappingLink?: (mapping: string) => ReactNode
  editable?: boolean
}) {
  const files = useDraft()
  const plan = useDraftProvisioningPlan(replication)
  const { staged, apply } = useProvisioningApply(project, id)
  const settings = provisioningOf(draft)
  const unsaved = files.dirtyPaths.filter(isQuestionPath).length

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }} data-testid="replication-provisioning">
      <div className="card">
        <div className="card-head">
          <span className="card-title">Target provisioning</span>
          <span className="card-note">the default for every table mapping in this replication</span>
        </div>
        <fieldset className="card-body" style={{ gap: 14, border: 0, margin: 0 }} disabled={!editable}>
          <InheritableToggle
            label="Create target table if missing"
            description="Creates the table only when it does not exist. Never alters one that does."
            value={settings.createTargetTableIfMissing}
            inherited={false}
            from="Sync's default"
            onChange={(next) => setDraft({ ...draft, provisioning: { ...settings, createTargetTableIfMissing: next } })}
            testId="task-provisioning-create"
          />
          <div className="divider" />
          <InheritableToggle
            label="Alter target columns if missing or changed"
            description="Adds a mapped column the target lacks and changes one whose type no longer matches. Never drops a column."
            value={settings.alterTargetTableColumnsIfMissingOrChanged}
            inherited={false}
            from="Sync's default"
            onChange={(next) => setDraft({ ...draft, provisioning: { ...settings, alterTargetTableColumnsIfMissingOrChanged: next } })}
            testId="task-provisioning-alter"
          />
        </fieldset>
      </div>

      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <span className="hint" data-testid="provisioning-plan-basis">
          {unsaved > 0
            ? `The plan is made from the draft as shown here, ${unsaved === 1 ? 'one unsaved edit' : `${unsaved} unsaved edits`} included, and the catalog cache. Apply runs the version on the Runner, which has none of these edits.`
            : 'The plan is made from the saved files and the catalog cache.'}
        </span>
        <button type="button" className="btn btn-sm spacer" disabled={plan.isFetching} onClick={() => void plan.refetch()} data-testid="provisioning-replan">
          {plan.isFetching ? 'Planning…' : 'Plan again'}
        </button>
      </div>
      <span className="hint" data-testid="provisioning-staged">{stagedLine(staged.data, staged.isLoading)}</span>
      <ErrorBanner error={plan.error ?? staged.error} />
      {plan.isLoading && <div className="empty">Planning…</div>}
      {plan.data && (
        <ReplicationProvisioningPanel
          replication={replication}
          answer={plan.data}
          mappingLink={mappingLink}
          onApply={staged.data && editable && !apply.isPending ? (target) => apply.mutate({ replication, ...target }) : undefined}
        />
      )}
      {apply.isPending && <div className="hint" data-testid="provisioning-applying">Applying on the Runner…</div>}
      <ErrorBanner error={apply.error} />
      {apply.data && <ProvisioningRunResult run={apply.data} />}
      <NewestPreflight project={project} id={id} replication={replication} />
    </div>
  )
}
