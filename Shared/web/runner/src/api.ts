import { ApiError, request, type TrustApi, type TrustState } from '@dbdatakit/shell'

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
/** `access` is the manifest's `access: read|write` (plan 21 CH03): a read slot a channel leaves unbound is served from production's binding. Absent from an older runner: write. */
export interface SlotRequirement { channel: string; slot: string; kind: string; access?: 'read' | 'write'; tasks: string[]; versions: number[] }

/** What `POST /api/v1/bindings/{channel}/integration/{name}/test` answers: the step that failed or, when `ok`, the last one tried (`binding`, `connection`, `login`, `connect`), in words, and whether a connection was really opened. */
export interface SlotTestAnswer { slot: string; bound_to: string | null; ok: boolean; step: 'binding' | 'connection' | 'login' | 'connect'; message: string; connected: boolean }

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

/** One part of a checkpoint as `GET .../checkpoints` gives it: its type (`int`, `text`, `time`, ...) and its value as text. */
export interface CheckpointPart { type: string; value: string }

/** A checkpoint of a task under one key (`''` when it is not kept per key). */
export interface CheckpointItem { key: string; version: number; updated: string; updated_by: string | null; parts: Record<string, CheckpointPart> }

export interface TaskCheckpoint { name: string; items: CheckpointItem[] }

/** One change in a checkpoint's history: a part went from `old` to `new` (null: it did not exist, or was removed), by a run or by an operator. */
export interface CheckpointChange { id: number; part: string; type: string; old: string | null; new: string | null; version: number; run_id: string | null; actor: string | null; at: string }

/** A step an earlier attempt began and left unanswered (`GET .../in-doubt`): it may or may not have done its work, and only a person can say. */
export interface InDoubtStep { chain: string; name: string; key: string; attempt: number; started: string; note: string | null }

/** What `POST .../steps/{name}/resolve` answers: the step is answered, and the run queued to go on when one was asked for. */
export interface StepAnswer { task: string; step: string; resolved: boolean; happened: boolean; run_id: string | null; retry_queued: boolean }

/** A record an outside system refused (`GET .../dead-letters`): why, how often, and whether a body was kept to send again. */
export interface DeadLetterRecord { id: string; source: string; reason: string; attempts: number; status: string; first: string; last: string; resolved: string | null; has_body: boolean }

export type DeadLetterStatus = 'open' | 'replayed' | 'discarded'

/** One unit of a run's attempt chain (`GET /runs/{id}/units`). */
export interface RunUnit { name: string; key: string; state: string; attempt: number; started: string; completed: string | null; duration_ms: number | null; rows: number | null; counts: Record<string, number>; error: string | null }

export interface UnitsSummary { total: number; done: number; skipped: number; failed: number; running: number }

export interface RunUnits { run_id: string; task: string; chain: string; summary: UnitsSummary; truncated: boolean; units: RunUnit[] }

export interface RunStep { name: string; key: string; state: string; attempt: number; started: string; completed: string | null; note: string | null }

/** A default the administrator may loosen (`GET /posture`): what it is today and what loosening it changes. */
export interface PostureSettingInfo { name: string; default: string; effect: string }

/** A recorded choice: who loosened which default, for one package or `*` (every package), and when. */
export interface PostureChoiceRecord { setting: string; scope: string; chosen_by: string; chosen_at: string; note: string | null }

export interface PostureState { all_packages: string; settings: PostureSettingInfo[]; choices: PostureChoiceRecord[] }

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
  /** Where the task got to in each outside source (plan 29a Q20): every checkpoint with its items. */
  checkpoints: (task: TaskKey) => request<{ task: string; checkpoints: TaskCheckpoint[] }>(`${taskPath(task)}/checkpoints`).then((r) => r.checkpoints),
  /** The changes to one checkpoint (one key of it), newest first. */
  checkpointHistory: (task: TaskKey, name: string, key = '') =>
    request<{ task: string; name: string; key: string; changes: CheckpointChange[] }>(`${taskPath(task)}/checkpoints/${enc(name)}/history?limit=50${key === '' ? '' : `&key=${enc(key)}`}`).then((r) => r.changes),
  /** Goes back to what the checkpoint was before one change of its history: a new version under the operator's name, audited. 409 when a run moved it meanwhile. */
  rewindCheckpoint: (task: TaskKey, name: string, historyId: number, key = '') => post<{ task: string; name: string; key: string; version: number | null }>(`${taskPath(task)}/checkpoints/${enc(name)}/rewind`, { history_id: historyId, key }),
  /** The steps of the task an earlier attempt left in doubt, across its attempt chains. */
  inDoubt: (task: TaskKey) => request<{ task: string; steps: InDoubtStep[] }>(`${taskPath(task)}/in-doubt`).then((r) => r.steps),
  /** The operator's answer for an in-doubt step: it happened (with its result as JSON) or it did not; `retry` also queues a run to go on. Audited. 409 when the step is not waiting. */
  resolveStep: (task: TaskKey, step: InDoubtStep, body: { happened: boolean; result?: unknown; retry: boolean }) =>
    post<StepAnswer>(`${taskPath(task)}/steps/${enc(step.name)}/resolve`, { chain: step.chain, key: step.key, happened: body.happened, ...(body.happened ? { result: body.result ?? null } : {}), retry: body.retry }),
  deadLetters: (task: TaskKey, status: DeadLetterStatus = 'open') => request<{ task: string; dead_letters: DeadLetterRecord[] }>(`${taskPath(task)}/dead-letters?status=${status}`).then((r) => r.dead_letters),
  /** Sends a dead letter again: its body becomes an inbox item of the task, once, and a run is queued. 409 when it is not open. */
  replayDeadLetter: (task: TaskKey, id: string) => post<{ id: string; replayed: boolean; inbox_item: string | null; run_queued: boolean }>(`${taskPath(task)}/dead-letters/${enc(id)}/replay`),
  discardDeadLetter: (task: TaskKey, id: string) => post<{ id: string; discarded: boolean }>(`${taskPath(task)}/dead-letters/${enc(id)}/discard`),
  /** The table of units of a run's attempt chain; 404 when the chain is not known any more. */
  runUnits: (id: string) => request<RunUnits>(`/api/v1/runs/${enc(id)}/units`),
  runSteps: (id: string) => request<{ run_id: string; steps: RunStep[] }>(`/api/v1/runs/${enc(id)}/steps`).then((r) => r.steps),
  posture: () => request<PostureState>('/api/v1/posture-choices'),
  /** An administrator loosens a default (`confirm` says they accepted the effect) or takes the choice back (`revoke`). Audited. */
  choosePosture: (body: { setting: string; scope: string; confirm?: boolean; revoke?: boolean; note?: string }) => request<PostureChoiceRecord | { revoked: boolean }>('/api/v1/posture-choices', { method: 'PUT', body: JSON.stringify(body) }),
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
  /** The publisher keys the runner trusts for integrations (plan 30 PB03); whether signatures are required is `setRequireSignatures`, not a per-kind switch. */
  trust: {
    get: (kind: string) => request<TrustState>(`/api/v1/trust/${encodeURIComponent(kind)}`),
    addKey: (kind: string, publicKey: string, label: string) => request<{ key_id: string }>(`/api/v1/trust/${encodeURIComponent(kind)}/keys`, { method: 'PUT', body: JSON.stringify({ public_key: publicKey, label: label || null }) }),
    revoke: (kind: string, keyId: string) => request<{ revoked: boolean }>(`/api/v1/trust/${encodeURIComponent(kind)}/${encodeURIComponent(keyId)}/revoke`, { method: 'POST' }),
  } satisfies TrustApi,
  setRequireSignatures: (value: boolean) => request<{ require_signatures: boolean }>('/api/v1/settings/require_signatures', { method: 'PUT', body: JSON.stringify({ value }) }),
  bind: (name: string, channel: string, body: BindRequest) =>
    request<{ name: string; channel: string; bindings: DefinitionBinding[] }>(`/api/v1/bindings/${enc(channel)}/integration/${enc(name)}`, { method: 'PUT', body: JSON.stringify(body) }),
  /** Tries one slot's binding as a run would (UI08): how far it got and, when it did not get through, why. Never a value. */
  tryBinding: (name: string, channel: string, slot: string) =>
    request<SlotTestAnswer>(`/api/v1/bindings/${enc(channel)}/integration/${enc(name)}/test`, { method: 'POST', body: JSON.stringify({ slot }) }),
  activate: (name: string, body: ActivateRequest) => post<ActivationAnswer>(`/api/v1/definitions/integration/${enc(name)}/activate`, body),
  switchTo: (name: string, body: ActivateRequest & { version: number }) => post<ActivationAnswer>(`/api/v1/definitions/integration/${enc(name)}/switch`, body),
  rollback: (name: string, body: ActivateRequest) => post<ActivationAnswer>(`/api/v1/definitions/integration/${enc(name)}/rollback`, body),
  retire: (name: string, body: ActivateRequest) => post<{ retired: boolean }>(`/api/v1/definitions/integration/${enc(name)}/retire`, body),
  /** Prune (plan 30 PB05): with `dry_run` true (the default of the call) it only says what would go. */
  prune: (name: string, body: { versions: number[]; dry_run: boolean }) => post<PruneAnswer>(`/api/v1/definitions/integration/${enc(name)}/prune`, body),
}

/** What a prune answers: the versions it removed (or would), the candidates it kept and why, and the files that go with them. */
export interface PruneAnswer { name: string; dry_run: boolean; remove: number[]; retained: { version: number; reason: string }[]; files: string[] }
