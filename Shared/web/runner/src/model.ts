import type { ActivateRequest, ChannelHead, InstanceStagingStatus, RunnerConnection, RunnerInstance, StagingInfo, TaskStatus } from './api'

export type TaskState = 'running' | 'queued' | 'paused' | 'waiting' | 'problem' | 'idle'

const count = (value: number | boolean) => (typeof value === 'boolean' ? (value ? 1 : 0) : value)

/** What to say about a task first: running, queued, held by a person, waiting for something (a label no runner has), a problem, or nothing. */
export function taskState(task: TaskStatus): TaskState {
  if (count(task.running) > 0) return 'running'
  if (task.paused) return 'paused'
  if (task.waiting_reason) return 'waiting'
  if (task.problem) return 'problem'
  if (count(task.queued) > 0) return 'queued'
  return 'idle'
}

export const taskKey = (task: { package: string; channel: string; task: string }) => `${task.package}/${task.channel}/${task.task}`

/** A run's outcome in words (the runner writes it in lower case without separators). */
export function outcomeText(outcome: string): string {
  switch (outcome) {
    case 'completedwithfailures': return 'Completed with failures'
    case '': return ''
    default: return outcome.charAt(0).toUpperCase() + outcome.slice(1)
  }
}

/** Whether an outcome is one to look at first. */
export const isBad = (outcome: string) => outcome === 'failed' || outcome === 'completedwithfailures' || outcome === 'interrupted'

/** An instance that is not live is gone (marked so, or silent for longer than its lease); an answer that does not say is live. */
export const isGone = (instance: RunnerInstance) => instance.live === false

/** What the machine says about itself that decides where a task can run: the limits it enforces and whether it reaches the outside. */
export function capabilityLines(capabilities: Record<string, string>): string[] {
  const lines: string[] = []
  if (capabilities.memory_limit) lines.push(`memory limit ${capabilities.memory_limit}`)
  if (capabilities.cpu_limit) lines.push(`cpu limit ${capabilities.cpu_limit}`)
  if (capabilities.network) lines.push(`network ${capabilities.network}`)
  if (capabilities.os) lines.push(capabilities.os)
  return lines
}

/** What a channel's cell says: the version it serves, and the one waiting behind it. */
export function headText(head: ChannelHead): string {
  const parts = [head.active === null ? 'nothing served' : `v${head.active} active`]
  if (head.previous !== null) parts.push(`v${head.previous} previous`)
  if (head.staged !== null) parts.push(`v${head.staged} staged`)
  return parts.join(', ')
}

/** How far the instances are with the staged version of a channel: the reports of the live ones, the gone ones left out. */
export function stagingRows(staging: StagingInfo, reports: InstanceStagingStatus[]): InstanceStagingStatus[] {
  return reports.filter((r) => r.channel === staging.channel && r.version === staging.version)
}

/**
 * The body of an activation. A version whose dry run lost progress is activated only with `confirm_losses` and the list echoed back (the runner refuses another list); an activation anyway (an instance failed or has not reported)
 * needs its reason. Returns null when what the person gave is not enough to send.
 */
export function activationBody(staging: StagingInfo, active: number | null, options: { confirmed: boolean; anyway: boolean; reason: string }): ActivateRequest | null {
  const body: ActivateRequest = { channel: staging.channel, expected_version: active ?? 0 }
  if (staging.needs_confirmation) {
    if (!options.confirmed) return null
    body.confirm_losses = true
    body.losses = staging.losses
  }
  if (options.anyway) {
    if (!options.reason.trim()) return null
    body.anyway = true
    body.reason = options.reason.trim()
  }
  return body
}

/** Whether an administrator needs the anyway path: the runners are not all ready. */
export const needsAnyway = (staging: StagingInfo) => !staging.all_ready

/** What a task connects to: the connections whose users include the task (`package/task`) in its channel, each with the role its use needs. */
export function connectionsOfTask(connections: RunnerConnection[], task: { package: string; channel: string; task: string }): { name: string; kind: string; role: string }[] {
  const wanted = `${task.package}/${task.task}`
  return connections.flatMap((c) => c.used_by.filter((u) => u.process === 'task' && u.name === wanted && u.channel === task.channel).map((u) => ({ name: c.name, kind: c.kind, role: u.role })))
}

/**
 * The versions a prune dialog preselects (plan 30 PB05, default taken): every version except the one any channel serves, has staged or served before, and the three newest. The server refuses the ones that are kept anyway; this only
 * saves the person from ticking them.
 */
export function pruneCandidates(versions: number[], heads: ChannelHead[], keepNewest = 3): number[] {
  const held = new Set<number>()
  for (const h of heads) for (const v of [h.active, h.previous, h.staged]) if (v !== null && v !== undefined) held.add(v)
  const newest = new Set([...versions].sort((a, b) => b - a).slice(0, keepNewest))
  return versions.filter((v) => !held.has(v) && !newest.has(v)).sort((a, b) => a - b)
}
