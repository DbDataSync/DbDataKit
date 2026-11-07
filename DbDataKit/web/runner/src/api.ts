import { request } from '@dbdatakit/shell'

/** One run as `GET /api/v1/runs` and a task's `last_run` give it. */
export interface RunRecord {
  id: string
  package: string
  package_version: number | null
  channel: string
  task: string
  attempt: number
  outcome: string
  started: string
  finished: string | null
  rows: number | null
  failed_item: string | null
  failure: { kind: string; message: string } | null
}

export interface TaskStatus {
  package: string
  channel: string
  task: string
  version: string | number | null
  next_due: string | null
  last_run: RunRecord | null
  running: number | boolean
  queued: number | boolean
  next_attempt_at: string | null
  paused: boolean
  paused_by: string | null
  problem: string | null
  required_labels: string[]
  waiting_reason: string | null
}

export interface InstanceLabel { label: string; source: 'admin' | 'reported' | string }

export interface RunnerInstance {
  id: string
  host: string
  version: string
  started: string
  heartbeat: string
  this: boolean
  /** Absent from an answer that lists live instances only. */
  live?: boolean
  state?: string
  labels: InstanceLabel[]
  capabilities: Record<string, string>
  running: number
}

export interface RunnerStatus {
  product: string
  version: string
  group: string
  running: boolean
  state_engine: string
  channels: string[]
  capabilities: Record<string, string>
  tasks: TaskStatus[]
  instances: RunnerInstance[]
  problems: { subject: string; message: string; at: string }[]
}

export interface ChannelHead { channel: string; active: number | null; previous: number | null; staged: number | null }

export interface DefinitionSummary { name: string; latest_version: number; owner: string | null; channels: ChannelHead[] }

export interface VersionInfo { version: number; hash: string; published_by: string; published_at: string; note: string | null; depends_on: string[]; files: number }

export interface StagingInfo { channel: string; version: number; summary: string; all_ready: boolean; failed: { instance: string; error: string }[]; losses: string[]; needs_confirmation: boolean }

export interface InstanceStagingStatus { instance: string; channel: string; version: number; state: string; error: string | null; note: string | null; has_losses: boolean; at: string; gone: boolean }

export interface DefinitionEvent { id: number; action: string; channel: string | null; version: number | null; actor: string | null; at: string; note: string | null }

export interface DefinitionDetail {
  name: string
  owner: string | null
  latest_version: number
  versions: VersionInfo[]
  channels: ChannelHead[]
  staging: StagingInfo[]
  bindings: { channel: string; slot: string; bound_to: string; revision: number }[]
  instances: InstanceStagingStatus[]
  events: DefinitionEvent[]
}

/** What an activation, a switch or a rollback answers: 200 when the version moved, 202 while it waits for the runs in flight. */
export interface ActivationAnswer { name: string; channel: string; active: number | null; state: string; message: string | null; losses: string[]; busy_tasks: string[] }

export interface RunFilters { package?: string; channel?: string; outcome?: string }

export const OUTCOMES = ['running', 'succeeded', 'completedwithfailures', 'failed', 'stopped', 'interrupted'] as const

const enc = encodeURIComponent

export function runsQuery(filters: RunFilters, limit = 100): string {
  const query = new URLSearchParams()
  if (filters.package) query.set('package', filters.package)
  if (filters.channel) query.set('channel', filters.channel)
  if (filters.outcome) query.set('outcome', filters.outcome)
  query.set('limit', String(limit))
  return `?${query}`
}

export interface ActivateRequest {
  channel: string
  /** What the channel serves now (0 for nothing): the runner refuses when it changed since the person looked. */
  expected_version: number
  confirm_losses?: boolean
  losses?: string[]
  anyway?: boolean
  reason?: string
}

interface TaskKey { package: string; channel: string; task: string }

const post = <T>(path: string, body?: unknown) => request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) })
const taskPath = (t: TaskKey) => `/api/v1/tasks/${enc(t.package)}/${enc(t.channel)}/${enc(t.task)}`

export const runnerApi = {
  status: () => request<RunnerStatus>('/api/v1/status'),
  /** The group's instances, those that are gone included. */
  instances: () => request<{ group: string; this: string | null; instances: RunnerInstance[] }>('/api/v1/instances?all=true').then((r) => r.instances),
  runs: (filters: RunFilters) => request<{ runs: RunRecord[] }>(`/api/v1/runs${runsQuery(filters)}`).then((r) => r.runs),
  run: (task: TaskKey) => post<{ run_id: string }>(`${taskPath(task)}/run`, {}),
  pause: (task: TaskKey) => post<unknown>(`${taskPath(task)}/pause`),
  resume: (task: TaskKey) => post<unknown>(`${taskPath(task)}/resume`),
  definitions: () => request<{ definitions: DefinitionSummary[] }>('/api/v1/definitions/integration').then((r) => r.definitions),
  definition: (name: string) => request<DefinitionDetail>(`/api/v1/definitions/integration/${enc(name)}`),
  activate: (name: string, body: ActivateRequest) => post<ActivationAnswer>(`/api/v1/definitions/integration/${enc(name)}/activate`, body),
  switchTo: (name: string, body: ActivateRequest & { version: number }) => post<ActivationAnswer>(`/api/v1/definitions/integration/${enc(name)}/switch`, body),
  rollback: (name: string, body: ActivateRequest) => post<ActivationAnswer>(`/api/v1/definitions/integration/${enc(name)}/rollback`, body),
  retire: (name: string, body: ActivateRequest) => post<{ retired: boolean }>(`/api/v1/definitions/integration/${enc(name)}/retire`, body),
}
