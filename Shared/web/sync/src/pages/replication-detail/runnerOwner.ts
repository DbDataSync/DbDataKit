import type { ReplicationOwner } from '../../api/types'
import { ApiError } from '../../api/client'

/** What each owner means, in the words the screen shows beside it. */
export const OWNER_TEXT: Record<ReplicationOwner, string> = {
  old: 'This server schedules the replication and runs its work in its own worker processes.',
  observe: 'This server still schedules and runs it. The runner works out when it would have queued a pass and records that; nothing runs on the runner.',
  runner: 'The runner schedules the replication and runs it as a drain. This server queues nothing for it and starts no worker; Run now, reloads, reconciles, verifications, resyncs, pause and resume go to the runner.',
}

/** The moves that make sense from an owner: observe from old, cutover from old or observe, rollback from observe or runner. */
export function movesFrom(owner: ReplicationOwner): { observe: boolean; cutover: boolean; rollback: boolean } {
  return { observe: owner === 'old', cutover: owner !== 'runner', rollback: owner !== 'old' }
}

/**
 * A move that the console's relay stopped waiting for (it answers 504 with `inProgress`): Sync keeps moving and the owner shown here is the answer, so this is not an error to show. Any other failure is.
 */
export function isMoveInProgress(error: unknown): boolean {
  return error instanceof ApiError && error.status === 504 && (error.body as { inProgress?: boolean } | null | undefined)?.inProgress === true
}

export const MOVE_IN_PROGRESS_TEXT = 'The move is in progress. The server is still waiting for work in flight and has not answered yet; the owner above changes when it finishes.'
