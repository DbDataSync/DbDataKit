import { request } from '@dbdatakit/shell'

export interface UpgradeStep { name: string; state: string; error: string | null; at: string | null }

/** The state of an upgrade of one server (plan 24a UC09): `GET /api/v1/upgrade` of the server, relayed. */
export type UpgradeState = 'none' | 'prepared' | 'draining' | 'applying' | 'committed' | 'rolled_back' | 'rejected' | 'failed'

export interface UpgradeStatus {
  product: string
  version: string
  generation: number
  install_method: string
  can_apply: boolean
  reason: string | null
  state: UpgradeState
  problem: string | null
  draining: boolean
  active_work: number
  message?: string | null
  plan?: { id: string; from: string; to: string; state: string; reason: string | null; steps: UpgradeStep[] }
}

/** What `prepare` answers: nothing was changed. `instruction` is the command for an install the console does not upgrade (an image, Scoop, WinGet, a tool; plan 24a QU3). */
export interface PrepareReport {
  state: 'prepared' | 'up_to_date' | 'instructions' | 'refused'
  product: string
  from: string
  to: string | null
  generation: number
  generation_of_release?: number | null
  install_method: string
  feed: string
  can_apply: boolean
  message: string
  reason?: string
  notes_url?: string | null
  problems?: string[]
  steps?: string[]
  artifact?: { name: string; size: number; sha256: string; verified: boolean }
  instruction?: { text: string; command: string | null }
}

/** The path of a server's upgrade API through the console's relay. */
export const upgradeBase = (id: string) => `/relay/${encodeURIComponent(id)}/api/v1/upgrade`

/** The products whose servers mount the upgrade endpoints (Sync's management API has its own sign-in and is not wired yet). */
export function offersUpgrade(product: string | null): boolean {
  return product === 'dbdatabuild' || product === 'dbdatagateway' || product === 'dbdatastore' || product === 'dbdatarunner'
}

export const upgradeRunApi = {
  status: (id: string) => request<UpgradeStatus>(upgradeBase(id)),
  prepare: (id: string, version?: string) => request<PrepareReport>(upgradeBase(id), { method: 'POST', body: JSON.stringify(version ? { action: 'prepare', version } : { action: 'prepare' }) }),
  apply: (id: string) => request<UpgradeStatus>(upgradeBase(id), { method: 'POST', body: JSON.stringify({ action: 'apply' }) }),
}

/** True while the helper is working and the console should keep asking. */
export const isRunning = (state: UpgradeState | undefined) => state === 'draining' || state === 'applying'

/** True when the upgrade has ended, one way or the other. */
export const isFinished = (state: UpgradeState | undefined) => state === 'committed' || state === 'rolled_back' || state === 'rejected' || state === 'failed'

const STEP_WORDS: Record<string, string> = {
  Verify: 'Check the staged release again',
  Stop: 'Stop the service',
  Backup: 'Back up the configuration',
  Migrate: 'Migrate',
  Repoint: 'Put the new program in place',
  Start: 'Start the service',
  HealthCheck: 'Wait for the new version to answer',
  Commit: 'Commit',
}

/** The lines of progress: the drain first (while it waits), then the helper's steps. */
export function progressLines(status: UpgradeStatus): { key: string; text: string; state: 'pending' | 'running' | 'done' | 'failed' }[] {
  const lines: { key: string; text: string; state: 'pending' | 'running' | 'done' | 'failed' }[] = []
  const drained = status.state !== 'draining' && status.state !== 'prepared' && status.state !== 'none'
  lines.push({
    key: 'drain',
    text: status.state === 'draining' ? `Stop taking new work and wait for ${status.active_work} piece(s) of work running` : 'Stop taking new work and finish the work running',
    state: status.state === 'draining' ? 'running' : drained ? 'done' : 'pending',
  })
  for (const step of status.plan?.steps ?? []) {
    const state = step.state === 'Done' ? 'done' : step.state === 'Running' ? 'running' : step.state === 'Failed' ? 'failed' : 'pending'
    lines.push({ key: step.name, text: STEP_WORDS[step.name] ?? step.name, state })
  }
  return lines
}

/** The sentence under the progress: what happened, in the server's words. */
export function outcomeSentence(status: UpgradeStatus): string | null {
  switch (status.state) {
    case 'committed': return status.message ?? `Upgraded to ${status.plan?.to ?? status.version}.`
    case 'rolled_back': return status.message ?? `The new version did not become healthy; ${status.plan?.from ?? 'the old version'} was put back.`
    case 'rejected': return status.message ?? 'The upgrade was refused before anything was changed.'
    case 'failed': return status.message ?? 'The upgrade failed and needs an administrator.'
    default: return null
  }
}

