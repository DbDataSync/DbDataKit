import { Link } from 'react-router-dom'
import { useUnpublishedChanges } from '../api/hooks'
import type { PublishState } from '../api/types'

/** Whether a replication was changed after the last publish (plan 28c E12g): its own files, or the shared ones (connections, libraries, drivers, files) which every replication uses. */
export function isUnpublished(state: PublishState | null | undefined, replication: string): boolean {
  return !!state && state.everPublished && state.unpublished && (state.sharedChanged || state.replications.includes(replication))
}

/**
 * The indicator that a replication has unpublished changes. A publish is explicit: an edited replication keeps running the version the runner has until somebody publishes, and the screens say so rather than publish on save.
 * Pure: the connected form is {@link UnpublishedChanges}.
 */
export function UnpublishedNotice({ state, replication }: { state: PublishState | null | undefined; replication: string }) {
  if (!isUnpublished(state, replication) || !state) return null
  const why = state.replications.includes(replication) ? 'This replication was changed' : 'The connections, libraries or drivers it uses were changed'
  return (
    <div className="banner warn" role="status" data-testid="unpublished-changes">
      <div><strong>Unpublished changes.</strong> {why} after {state.version != null ? `version ${state.version}` : 'the last publish'} was published; a runner runs the published version until the replication set is published again.</div>
      <div style={{ marginTop: 4 }}><Link to="/runner">Publish on the Runner screen</Link></div>
    </div>
  )
}

/** A small tag for a list row. */
export function UnpublishedTag({ state, replication }: { state: PublishState | null | undefined; replication: string }) {
  if (!isUnpublished(state, replication)) return null
  return <span className="faint" style={{ marginLeft: 6, color: 'var(--warn)' }} data-testid={`unpublished-tag-${replication}`} title="Changed after the last publish to the runner">· unpublished changes</span>
}

export function UnpublishedChanges({ replication }: { replication: string }) {
  const { data } = useUnpublishedChanges()
  return <UnpublishedNotice state={data} replication={replication} />
}
