import type { ReplicationOwner } from '../../api/types'

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
