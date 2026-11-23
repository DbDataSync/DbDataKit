import { useState } from 'react'
import { Link, Navigate, useNavigate } from '@dbdatakit/shell'
import { designerPath, errorsUnder, filesPath, replicationsOf, useDraft, useDraftCheck, type DesignerProps, type DesignerRegistration } from '@dbdatakit/workspace'
import { NewReplicationForm } from './replication/NewReplicationForm'
import { ReplicationPage } from './pages/ReplicationPage'

/** The replication a Designer address names (`replications/<name>/...`) and the rest of the address after it, or null. */
export function replicationOfRest(rest: string): { name: string; rest: string } | null {
  const m = /^replications\/([^/]+)(?:\/(.*))?$/.exec(rest)
  return m ? { name: decodeURIComponent(m[1]), rest: m[2] ?? '' } : null
}

/**
 * The sync Designer's entries in the navigation: the draft's replications, each opening its page with the count of the draft's check's errors in
 * its files (the check of what is saved), and **New replication** last, as a dialog.
 */
function SyncNavigation({ project, id, rest }: DesignerProps) {
  const draft = useDraft()
  const check = useDraftCheck(project, id)
  const navigate = useNavigate()
  const [making, setMaking] = useState(false)
  const open = replicationOfRest(rest)?.name ?? null
  const replications = replicationsOf(draft.paths)
  return (
    <div data-testid="sd-nav">
      {replications.length === 0 && <div className="hint" data-testid="sd-nav-none">No replication yet.</div>}
      {replications.map((r) => (
        <Link key={r} to={designerPath(project, id, `replications/${r}`)} className={`grid-row mono ${open === r ? 'active' : ''}`} style={{ display: 'block' }}
          aria-current={open === r ? 'page' : undefined} data-testid="sd-nav-replication" data-name={r}>
          {r}{draft.dirtyPaths.some((p) => p.startsWith(`config/replications/${r}/`)) ? ' •' : ''}
          <ErrorCount count={errorsUnder(check.data?.diagnostics, `config/replications/${r}/`)} />
        </Link>
      ))}
      {draft.canEdit && (
        <button type="button" className="btn-link" style={{ marginTop: 6 }} onClick={() => setMaking(true)} data-testid="sd-new-replication">New replication</button>
      )}
      {making && (
        <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) setMaking(false) }}>
          <div className="modal" role="dialog" aria-modal="true" aria-label="New replication" data-testid="sd-new-replication-dialog">
            <NewReplicationForm
              onCancel={() => setMaking(false)}
              onCreated={(name) => { setMaking(false); navigate(designerPath(project, id, `replications/${name}`)) }}
            />
          </div>
        </div>
      )}
    </div>
  )
}

/** The check's errors in a replication's files, as a badge; nothing when there are none. */
function ErrorCount({ count }: { count: number }) {
  if (count === 0) return null
  return <span className="badge" style={{ marginLeft: 6, borderColor: 'var(--danger-edge)', color: 'var(--danger-ink)' }} title={`The draft's check finds ${count} error${count === 1 ? '' : 's'} in this replication's files.`} data-testid="sd-nav-errors" data-count={count}>{count}</span>
}

/** The sync Designer's pages: a replication's page, and on the Designer's own address the first replication. */
function SyncPage({ project, id, rest }: DesignerProps) {
  const draft = useDraft()
  const replications = replicationsOf(draft.paths)
  const open = replicationOfRest(rest)
  if (open === null) {
    if (draft.filesLoading) return <div className="empty">Loading…</div>
    if (replications.length > 0) return <Navigate to={designerPath(project, id, `replications/${replications[0]}`)} replace />
    return <div className="empty" data-testid="sd-empty">This draft has no replication yet. Make one with New replication in the navigation.</div>
  }
  if (!replications.includes(open.name)) {
    if (draft.filesLoading) return <div className="empty">Loading…</div>
    return <div className="empty" data-testid="sd-missing">There is no replication {open.name} in this draft. <Link to={filesPath(project, id, 'config/replications')}>Its files</Link></div>
  }
  return <ReplicationPage key={open.name} project={project} id={id} replication={open.name} rest={open.rest} />
}

/**
 * A sync project's Designer: what lives inside a replication, restored from the Sync server's authoring screens (`DbDataWorkspace/README.md`, "A
 * project's two areas: Files and Designer"). A host registers it with `registerDesigner` of `@dbdatakit/workspace`.
 */
export const syncDesigner: DesignerRegistration = { kind: 'sync', title: 'Designer', Navigation: SyncNavigation, Page: SyncPage }
