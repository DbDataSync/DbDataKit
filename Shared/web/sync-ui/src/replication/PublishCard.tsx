import { Link } from '@dbdatakit/shell'
import { buildsPath, useDraft } from '@dbdatakit/workspace'

/** The sentence about the draft's unsaved edits, null when it has none: a build reads the files the Workspace server holds, so an edit kept only in the browser is not in it. */
export function unsavedNote(count: number): string | null {
  if (count === 0) return null
  return `${count === 1 ? 'One edit' : `${count} edits`} of this draft ${count === 1 ? 'is' : 'are'} not saved, so a build leaves ${count === 1 ? 'it' : 'them'} out. Save first to build ${count === 1 ? 'it' : 'them'}.`
}

/**
 * The rail's card that publishes the replication: a Runner runs the published build of the whole set, so **Build and publish…** opens the Files
 * area's Builds tab of the same draft (`buildsPath`), where the build is made and its publish panel deploys it through the console. It is a link,
 * so the browser's Back returns here. With unsaved edits the card says a build leaves them out.
 */
export function PublishCard({ project, id }: { project: string; id: string }) {
  const draft = useDraft()
  const note = unsavedNote(draft.dirtyPaths.length)
  return (
    <div className="card" data-testid="sd-publish-card">
      <div className="card-body">
        <div className="hint" style={{ marginBottom: 8 }}>
          A Runner runs what was published, not this draft. A build holds every replication of the set, and is published from the Builds tab.
        </div>
        {note && <div className="hint" style={{ marginBottom: 8, color: 'var(--warn)' }} data-testid="sd-publish-unsaved" data-count={draft.dirtyPaths.length}>{note}</div>}
        <Link to={buildsPath(project, id)} className="btn btn-primary" data-testid="sd-build-and-publish">Build and publish…</Link>
      </div>
    </div>
  )
}
