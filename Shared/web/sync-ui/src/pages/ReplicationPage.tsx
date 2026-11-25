import { useState } from 'react'
import { ErrorBanner, Link, useNavigate } from '@dbdatakit/shell'
import { designerPath, filesPath, useDraft } from '@dbdatakit/workspace'
import { taskPath } from '../model'
import { useSyncDraft, type FileProblem } from '../useSyncDraft'
import { PublishCard } from '../replication/PublishCard'
import { ReplicationChecksPanel } from '../replication/ReplicationChecksPanel'
import { OverviewPanel } from './OverviewPanel'
import { TableMappingsPanel } from './TableMappingsPanel'

/** The replication's tabs and where each is: Overview at the replication's own address (its sub-tabs under `overview/`), Table Mappings under `mappings`, Quality Checks under `checks`. */
const TABS = [
  { tab: 'overview', path: '', label: 'Overview', testId: 'tab-overview' },
  { tab: 'mappings', path: 'mappings', label: 'Table Mappings', testId: 'tab-mappings' },
  { tab: 'checks', path: 'checks', label: 'Quality Checks', testId: 'tab-checks' },
] as const

export type ReplicationTab = (typeof TABS)[number]['tab']

/** Which tab an address after `replications/<name>/` opens. */
export const tabOf = (rest: string): ReplicationTab =>
  rest === 'mappings' || rest.startsWith('mappings/') ? 'mappings' : rest === 'checks' || rest.startsWith('checks/') ? 'checks' : 'overview'

/**
 * A replication's page in the Designer: the breadcrumbs, the tabs Overview, Table Mappings and Quality Checks, and the chrome. **Enabled** changes `enabled` of
 * the draft's `task.yaml` (it takes effect when the set is published); **Save settings** saves the task file; **Delete** removes the replication's
 * folder from the draft, after asking twice. Every edit on the page is an unsaved edit of the draft, the same one Files shows. The rail holds
 * **Build and publish…** (`PublishCard`); run controls and the status of a running replication are the Runner's, not the Designer's.
 */
export function ReplicationPage({ project, id, replication, rest }: { project: string; id: string; replication: string; rest: string }) {
  const draft = useDraft()
  const model = useSyncDraft(replication)
  const navigate = useNavigate()
  const [saveError, setSaveError] = useState<unknown>(null)
  const [deleting, setDeleting] = useState<0 | 1 | 2>(0)
  const [deleteError, setDeleteError] = useState<unknown>(null)
  const tab = tabOf(rest)
  const base = `replications/${replication}`
  const task = model.task.value
  const tPath = taskPath(replication)
  const folder = `config/replications/${replication}/`
  const files = draft.paths.filter((p) => p.startsWith(folder))
  const editable = draft.canEdit

  const save = async () => {
    setSaveError(null)
    try { await draft.saveOne(tPath) } catch (e) { setSaveError(e) }
  }
  const remove = async () => {
    setDeleteError(null)
    try {
      for (const p of files) await draft.remove(p)
      navigate(designerPath(project, id))
    } catch (e) { setDeleteError(e) }
  }

  return (
    <div data-testid="sd-replication" data-name={replication}>
      <div className="crumbs" data-testid="sd-crumbs">
        <span className="hint">Replications</span> <span className="hint">/</span> <h2 className="mono" style={{ display: 'inline', margin: 0 }}>{replication}</h2>
        {model.task.dirty && <span className="badge" style={{ marginLeft: 8 }} data-testid="sd-task-dirty">not saved</span>}
      </div>

      <div className="tabbar" style={{ marginTop: 8 }}>
        {TABS.map((t) => (
          <Link key={t.testId} to={designerPath(project, id, t.path ? `${base}/${t.path}` : base)} className={`tab ${tab === t.tab ? 'active' : ''}`}
            aria-current={tab === t.tab ? 'page' : undefined} data-testid={t.testId}>
            {t.label}{t.tab === 'checks' && (task?.checks?.length ?? 0) > 0 ? <span className="subtab-badge" data-testid="tab-checks-count">{task!.checks!.length}</span> : null}
          </Link>
        ))}
        <div className="actions">
          <span className="row" style={{ gap: 7, marginRight: 4 }}>
            <button
              type="button"
              className={`toggle ${task?.enabled ? 'on' : ''}`}
              onClick={() => task && model.setTask({ ...task, enabled: !task.enabled })}
              aria-pressed={task?.enabled ?? false}
              disabled={!task || !editable}
              title="Whether this replication runs at all. A change here is an edit of the draft: it takes effect on a Runner when the set is published."
              data-testid="enabled-toggle"
            />
            <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>{task?.enabled ? 'Enabled' : 'Disabled'}</span>
          </span>
          {editable && (
            <button type="button" className="btn btn-primary btn-chrome" onClick={() => void save()} disabled={!model.task.dirty || draft.saving} data-testid="save-settings-button">
              Save settings
            </button>
          )}
          {editable && deleting === 0 && (
            <button type="button" className="btn btn-danger btn-chrome" onClick={() => setDeleting(1)} data-testid="delete-replication-button">Delete</button>
          )}
        </div>
      </div>

      {deleting > 0 && (
        <div className="banner warn" role="alert" data-testid="delete-replication-ask" data-step={deleting}>
          {deleting === 1
            ? <span>Delete the replication <strong>{replication}</strong> and its {model.mappingNames.length} table mapping{model.mappingNames.length === 1 ? '' : 's'} from this draft?</span>
            : <span>This removes {files.length} file{files.length === 1 ? '' : 's'} under <span className="mono">{folder}</span> from the draft now, unsaved edits included; they come back only from a commit that has them.</span>}
          <span className="row" style={{ gap: 8 }}>
            <button type="button" className="btn btn-danger" onClick={() => (deleting === 1 ? setDeleting(2) : void remove())} data-testid="delete-replication-confirm">
              {deleting === 1 ? 'Yes, delete it' : 'Delete the files'}
            </button>
            <button type="button" className="btn" onClick={() => setDeleting(0)} data-testid="delete-replication-cancel">Keep it</button>
          </span>
        </div>
      )}

      <ErrorBanner error={saveError ?? deleteError} />
      <ProblemList project={project} id={id} problems={model.problems} />

      <div className="detail-body">
        <div className="detail-main">
          {model.task.loading && <div className="empty">Loading…</div>}
          {task && tab === 'overview' && (
            <OverviewPanel project={project} id={id} replication={replication} rest={rest} draft={task} setDraft={model.setTask}
              connections={model.connections} mappingCount={model.mappingNames.length} mappingNames={model.mappingNames} editable={editable} />
          )}
          {task && tab === 'mappings' && (
            <TableMappingsPanel project={project} id={id} replication={replication} model={model} rest={rest === 'mappings' ? '' : rest.slice('mappings/'.length)} />
          )}
          {task && tab === 'checks' && (
            <ReplicationChecksPanel checks={task.checks ?? []} onChange={(checks) => model.setTask({ ...task, checks })} editable={editable} />
          )}
        </div>
        <div className="detail-rail" data-testid="sd-rail">
          <PublishCard project={project} id={id} replication={replication} />
        </div>
      </div>
    </div>
  )
}

/** Each file the Designer cannot show, with a link that opens it in Files. */
export function ProblemList({ project, id, problems }: { project: string; id: string; problems: FileProblem[] }) {
  if (problems.length === 0) return null
  return (
    <div className="banner warn" data-testid="sd-problems">
      {problems.map((p) => (
        <div key={p.path} data-testid="sd-problem" data-path={p.path}>
          {p.message} <Link to={filesPath(project, id, p.path)} className="mono" data-testid="sd-problem-open">Open {p.path} in Files</Link>
        </div>
      ))}
    </div>
  )
}
