import { Link, Navigate } from '@dbdatakit/shell'
import { useDraft } from '../draft'
import { designerPath, filesPath, type DesignerProps, type DesignerRegistration } from '../designers'
import { replicationPath, replicationsOf } from '../syncSchemas'

/** The replication a Designer address names (`replications/<name>/...`), or null. */
export const replicationOfRest = (rest: string): string | null => {
  const m = /^replications\/([^/]+)/.exec(rest)
  return m ? decodeURIComponent(m[1]) : null
}

/** The sync Designer's entries in the navigation: the draft's replications, each opening its page. */
function SyncNavigation({ project, id, rest }: DesignerProps) {
  const draft = useDraft()
  const open = replicationOfRest(rest)
  const replications = replicationsOf(draft.paths)
  return (
    <div data-testid="sd-nav">
      {replications.length === 0 && <div className="hint" data-testid="sd-nav-none">No replication yet.</div>}
      {replications.map((r) => (
        <Link key={r} to={designerPath(project, id, `replications/${r}`)} className={`grid-row mono ${open === r ? 'active' : ''}`} style={{ display: 'block' }}
          aria-current={open === r ? 'page' : undefined} data-testid="sd-nav-replication" data-name={r}>{r}</Link>
      ))}
    </div>
  )
}

/** The sync Designer's pages: a replication's page (its screens are being restored; meanwhile its files open in Files), and the landing on the first replication. */
function SyncPage({ project, id, rest }: DesignerProps) {
  const draft = useDraft()
  const replications = replicationsOf(draft.paths)
  const open = replicationOfRest(rest)
  if (open === null) {
    if (draft.filesLoading) return <div className="empty">Loading...</div>
    if (replications.length > 0) return <Navigate to={designerPath(project, id, `replications/${replications[0]}`)} replace />
    return <div className="empty" data-testid="sd-empty">This draft has no replication yet.</div>
  }
  if (!replications.includes(open)) {
    return <div className="empty" data-testid="sd-missing">There is no replication {open} in this draft. <Link to={filesPath(project, id, 'config/replications')}>Its files</Link></div>
  }
  const files = draft.paths.filter((p) => p.startsWith(`config/replications/${open}/`))
  return (
    <div data-testid="sd-replication" data-name={open}>
      <h2 className="mono">{open}</h2>
      <div className="hint" data-testid="sd-placeholder">The replication's own page is not built yet. Its files open in Files:</div>
      {files.map((p) => <div key={p}><Link to={filesPath(project, id, p)} className="mono" data-testid="sd-open-file" data-path={p}>{p}{draft.dirtyPaths.includes(p) ? ' (not saved)' : ''}</Link></div>)}
      <div className="hint" style={{ marginTop: 8 }}>The task file is <Link to={filesPath(project, id, replicationPath(open))} className="mono">{replicationPath(open)}</Link>.</div>
    </div>
  )
}

/** A sync project's Designer: what lives inside a replication (`DbDataWorkspace/README.md`, "A project's two areas"). */
export const syncDesigner: DesignerRegistration = { kind: 'sync', title: 'Designer', Navigation: SyncNavigation, Page: SyncPage }
