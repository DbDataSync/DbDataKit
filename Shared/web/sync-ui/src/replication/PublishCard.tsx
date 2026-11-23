import { ErrorBanner, Link } from '@dbdatakit/shell'
import { buildsPath, useDraft, useDraftPublication, type DraftPublication } from '@dbdatakit/workspace'
import { UnpublishedNotice, type PublishState } from '../components/UnpublishedChanges'

/** The sentence about the draft's unsaved edits, null when it has none: a build reads the files the Workspace server holds, so an edit kept only in the browser is not in it. */
export function unsavedNote(count: number): string | null {
  if (count === 0) return null
  return `${count === 1 ? 'One edit' : `${count} edits`} of this draft ${count === 1 ? 'is' : 'are'} not saved, so a build leaves ${count === 1 ? 'it' : 'them'} out. Save first to build ${count === 1 ? 'it' : 'them'}.`
}

/** Where the draft stands against its newest deploy to a Runner, with the paths that have an unsaved edit; undefined while it is asked. */
export function publishStateOf(publication: DraftPublication | undefined, unsaved: readonly string[]): PublishState | undefined {
  if (!publication) return undefined
  const v = publication.version
  return {
    published: v ? { build: v.build, version: v.version, channel: v.channel, runner: v.targetName, mode: v.mode } : null,
    commitsSince: publication.commitsSince,
    unsaved,
  }
}

/**
 * The rail's card that publishes the replication: a Runner runs the published build of the whole set, so **Build and publish…** opens the Files
 * area's Builds tab of the same draft (`buildsPath`), where the build is made and its publish panel deploys it through the console. It is a link,
 * so the browser's Back returns here. With unsaved edits the card says a build leaves them out. Above it, **Unpublished changes** says what of this
 * replication the Runner does not run yet: the draft's commits after the build the console last deployed to a Runner, and the unsaved edits of this
 * replication and of the files every replication uses (`UnpublishedNotice`); nothing is said when no build of the draft was deployed.
 */
export function PublishCard({ project, id, replication }: { project: string; id: string; replication: string }) {
  const draft = useDraft()
  const publication = useDraftPublication(project, id)
  const note = unsavedNote(draft.dirtyPaths.length)
  return (
    <div className="card" data-testid="sd-publish-card">
      <div className="card-body">
        <UnpublishedNotice state={publishStateOf(publication.data, draft.dirtyPaths)} replication={replication} />
        <ErrorBanner error={publication.error} />
        <div className="hint" style={{ marginBottom: 8 }}>
          A Runner runs what was published, not this draft. A build holds every replication of the set, and is published from the Builds tab.
        </div>
        {note && <div className="hint" style={{ marginBottom: 8, color: 'var(--warn)' }} data-testid="sd-publish-unsaved" data-count={draft.dirtyPaths.length}>{note}</div>}
        <Link to={buildsPath(project, id)} className="btn btn-primary" data-testid="sd-build-and-publish">Build and publish…</Link>
      </div>
    </div>
  )
}
