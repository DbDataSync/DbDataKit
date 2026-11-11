import { ApiError, request } from '@dbdatakit/shell'

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

import type { RunLogLine } from './logs'

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
  /** Who schedules the task: `old` (the product that published it), `observe` (it does, and this runner records what it would have done) or `runner`. Absent from a runner that predates ownership. */
  owner?: string
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
  /** Present on the runner embedded in the console (plan 28a R20): what it serves and whether production was chosen. */
  embedded?: boolean
  console_runner?: { channels: string[]; production_accepted: boolean; production_warning: string | null; account_separate: boolean; max_concurrent: number }
}

export interface ChannelHead { channel: string; active: number | null; previous: number | null; staged: number | null }

export interface DefinitionSummary { name: string; latest_version: number; owner: string | null; channels: ChannelHead[] }

export interface VersionInfo { version: number; hash: string; published_by: string; published_at: string; note: string | null; depends_on: string[]; files: number }

export interface StagingInfo { channel: string; version: number; summary: string; all_ready: boolean; failed: { instance: string; error: string }[]; losses: string[]; needs_confirmation: boolean }

export interface InstanceStagingStatus { instance: string; channel: string; version: number; state: string; error: string | null; note: string | null; has_losses: boolean; at: string; gone: boolean }

export interface DefinitionEvent { id: number; action: string; channel: string | null; version: number | null; actor: string | null; at: string; note: string | null }

/** A connection slot a channel's served or staged version declares (`slots` of the definition detail): its kind, the tasks that use it and the versions that declare it. */
export interface SlotRequirement { channel: string; slot: string; kind: string; tasks: string[]; versions: number[] }

export interface DefinitionBinding { channel: string; slot: string; bound_to: string; revision: number }

/** `GET /api/v1/settings`: the publishing settings of the runner. */
export interface PublishSettings { require_signatures: boolean; max_artifact_bytes: number }

/** What `PUT /api/v1/bindings/{channel}/integration/{name}` takes: slot to the name of the connection (or secret reference) it is bound to, never a value. */
export interface BindRequest { slots: Record<string, string>; expected_revision?: number }

export interface DefinitionDetail {
  name: string
  owner: string | null
  latest_version: number
  versions: VersionInfo[]
  channels: ChannelHead[]
  staging: StagingInfo[]
  bindings: DefinitionBinding[]
  /** What each channel's manifests declare; absent from a runner that predates it. */
  slots?: SlotRequirement[]
  instances: InstanceStagingStatus[]
  events: DefinitionEvent[]
}

/** What an activation, a switch or a rollback answers: 200 when the version moved, 202 while it waits for the runs in flight. */
export interface ActivationAnswer { name: string; channel: string; active: number | null; state: string; message: string | null; losses: string[]; busy_tasks: string[] }

/** One connection a runner's packages are bound to, as `GET /api/v1/connections` gives it (plan 28c E23): the name or reference the channel binds, the kind of slot, and who uses it. Never a secret value. */
export interface RunnerConnection {
  name: string
  kind: string
  secret_ref: string | null
  roles: string[]
  used_by: { process: string; name: string; version: string | null; group: string | null; instance: string | null; channel: string | null; role: string }[]
}

/** `GET .../tasks/{package}/{channel}/{task}/schedule`: who schedules a task, since when and why, and where its schedule stands. */
export interface TaskSchedule {
  task: string
  owner: string
  owner_set_by: string | null
  owner_set_at: string | null
  reason: string | null
  handled_up_to: string | null
  paused: boolean
  paused_by: string | null
  paused_at: string | null
  running: number
  queued: number
  version: string | number | null
}

/** One scheduling decision of an observed task: `runner` is what this runner would have queued, `old` what the product's own scheduler queued. */
export interface TaskDecision { source: 'runner' | 'old' | string; at: string; missed: number; paused: boolean }

/** What moving a task to another owner sends: the reason is recorded, the position is the time up to which the old scheduler has dealt with (needed to hand a task to this runner). */
export interface OwnerBody { owner: string; reason: string; handled_up_to?: string }

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
  connections: () => request<{ connections: RunnerConnection[] }>('/api/v1/connections').then((r) => r.connections),
  runs: (filters: RunFilters) => request<{ runs: RunRecord[] }>(`/api/v1/runs${runsQuery(filters)}`).then((r) => r.runs),
  /** A run's output, the lines after the one with id `after` (from the start when none), at most `limit`; read again with the last id to follow a run that is going. */
  runLog: (id: string, after?: number, limit = 200) => request<{ run: string; lines: RunLogLine[] }>(`/api/v1/runs/${enc(id)}/logs?limit=${limit}${after === undefined ? '' : `&after=${after}`}`),
  /** Starts a run now; the inputs are the task's own (`name=value` of `tasks run`), as text. */
  run: (task: TaskKey, inputs: Record<string, string> = {}) => post<{ run_id: string }>(`${taskPath(task)}/run`, Object.keys(inputs).length > 0 ? { inputs } : {}),
  schedule: (task: TaskKey) => request<TaskSchedule>(`${taskPath(task)}/schedule`),
  decisions: (task: TaskKey) => request<{ task: string; decisions: TaskDecision[] }>(`${taskPath(task)}/decisions`).then((r) => r.decisions),
  setOwner: (task: TaskKey, body: OwnerBody) => request<{ task: string; owner: string }>(`${taskPath(task)}/owner`, { method: 'PUT', body: JSON.stringify(body) }),
  pause: (task: TaskKey) => post<unknown>(`${taskPath(task)}/pause`),
  resume: (task: TaskKey) => post<unknown>(`${taskPath(task)}/resume`),
  definitions: () => request<{ definitions: DefinitionSummary[] }>('/api/v1/definitions/integration').then((r) => r.definitions),
  definition: (name: string) => request<DefinitionDetail>(`/api/v1/definitions/integration/${enc(name)}`),
  /** Whether the runner holds the file with this hash (`HEAD`: 200 or 404; anything else is a refusal). */
  hasFile: (sha256: string) => request<unknown>(`/api/v1/artifacts/${sha256}`, { method: 'HEAD' }).then(() => true, (e) => (e instanceof ApiError && e.status === 404 ? false : Promise.reject(e))),
  /** Sends the bytes of a file under its hash; the runner refuses bytes that do not hash to it. */
  putFile: (sha256: string, body: Blob) => request<{ sha256: string; size: number; already_stored: boolean }>(`/api/v1/artifacts/${sha256}`, { method: 'PUT', body, headers: { 'Content-Type': 'application/octet-stream' } }),
  publishPackage: (name: string, body: unknown) =>
    request<{ kind: string; name: string; channel: string; version: number; changed: boolean; mode: string; signature: string; tasks: string[] }>(`/api/v1/definitions/integration/${enc(name)}`, { method: 'PUT', body: JSON.stringify(body) }),
  /** Sets the administrator's labels of a running instance to exactly this list; the answer says what was added and removed. */
  setLabels: (id: string, labels: string[]) =>
    request<{ instance: string; host: string; labels: InstanceLabel[]; added: string[]; removed: string[] }>(`/api/v1/instances/${enc(id)}/labels`, { method: 'PUT', body: JSON.stringify({ labels }) }),
  settings: () => request<PublishSettings>('/api/v1/settings'),
  setRequireSignatures: (value: boolean) => request<{ require_signatures: boolean }>('/api/v1/settings/require_signatures', { method: 'PUT', body: JSON.stringify({ value }) }),
  bind: (name: string, channel: string, body: BindRequest) =>
    request<{ name: string; channel: string; bindings: DefinitionBinding[] }>(`/api/v1/bindings/${enc(channel)}/integration/${enc(name)}`, { method: 'PUT', body: JSON.stringify(body) }),
  activate: (name: string, body: ActivateRequest) => post<ActivationAnswer>(`/api/v1/definitions/integration/${enc(name)}/activate`, body),
  switchTo: (name: string, body: ActivateRequest & { version: number }) => post<ActivationAnswer>(`/api/v1/definitions/integration/${enc(name)}/switch`, body),
  rollback: (name: string, body: ActivateRequest) => post<ActivationAnswer>(`/api/v1/definitions/integration/${enc(name)}/rollback`, body),
  retire: (name: string, body: ActivateRequest) => post<{ retired: boolean }>(`/api/v1/definitions/integration/${enc(name)}/retire`, body),
}
