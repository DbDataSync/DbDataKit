import { request } from '@dbdatakit/shell'

/** The latest result of one check and whether it blocks now (plan 23a FQ17); `server` is the server that evaluates it, `server_id` what the relay is addressed by. */
export interface QualityCheck {
  check_id: string
  severity: 'warn' | 'block'
  on_error: 'alert' | 'block'
  status: 'pass' | 'fail' | 'error'
  status_since: string
  measured: number | null
  threshold: number | null
  evaluated_at: string
  blocking: boolean
  blocked_since: string | null
  /** An administrator released the block this check set (plan 23a FQ17); the check still fails. */
  released_since?: string | null
  released_by?: string | null
  server: string
  server_id: string
  product: string | null
}

export interface QualityDataset { dataset: string; blocking: number; checks: QualityCheck[] }

/** What one server said when asked: whether it can evaluate on request, and when it cannot, how its checks are evaluated. */
export interface QualityServer { server: string; server_id: string; product: string | null; error: string | null; checks: number | null; can_run: boolean; run_note: string | null; can_release?: boolean }

export interface QualityAnswer { datasets: QualityDataset[]; servers: QualityServer[] }

export interface QualityResult { id: string; status: QualityCheck['status']; evaluated_at: string; measured: number | null; threshold: number | null; sample_keys: string[]; run_id: string | null; message: string | null }

/** What a server said to "evaluate now": the counts when it evaluated at once, or (a Runner, plan 35 I3a) the run of the package's `check` task it queued. */
export interface QualityRunAnswer { replication?: string; dataset: string; evaluated?: number; passed?: number; failed?: number; errors?: number; blocking?: number; notes?: string[]; queued?: boolean; run_id?: string; task?: string }

/** The words for an answer to "evaluate now". */
export function runAnswerLine(serverName: string, answer: QualityRunAnswer): string {
  if (answer.queued) return `${serverName} started a re-check: the task ${answer.task ?? 'check'} (run ${answer.run_id ?? '?'}) evaluates the checks again with its package's logins and loads nothing. Ask the servers again when it ends; a block clears if the checks pass.`
  const evaluated = answer.evaluated ?? 0
  const notes = answer.notes ?? []
  return `${serverName} evaluated ${evaluated} check${evaluated === 1 ? '' : 's'}: ${answer.passed ?? 0} passed, ${answer.failed ?? 0} failed, ${answer.errors ?? 0} could not run.${notes.length > 0 ? ' ' + notes.join(' ') : ''}`
}

/** What a dataset is as a whole, worst first: blocked, failing (a warn check failed), could not be checked, passing. */
export type DatasetState = 'blocked' | 'failing' | 'error' | 'pass'

const RANK: Record<DatasetState, number> = { blocked: 0, failing: 1, error: 2, pass: 3 }

export function datasetState(dataset: QualityDataset): DatasetState {
  if (dataset.checks.some((c) => c.blocking)) return 'blocked'
  if (dataset.checks.some((c) => c.status === 'fail')) return 'failing'
  if (dataset.checks.some((c) => c.status === 'error')) return 'error'
  return 'pass'
}

/** Blocked datasets first, then failing, then those that could not be checked, then passing; by name within a state. */
export const sortDatasets = (datasets: QualityDataset[]) =>
  [...datasets].sort((a, b) => RANK[datasetState(a)] - RANK[datasetState(b)] || a.dataset.localeCompare(b.dataset))

/** A word for a person, not the stored value. */
export const stateWord = (state: DatasetState) => ({ blocked: 'Blocked', failing: 'Failing', error: 'Could not be checked', pass: 'Passing' })[state]

/** The measure of a check as a sentence: what was found against what the check allows (the unit is the kind's: failing rows, seconds, percent). */
export function measureLine(check: Pick<QualityCheck, 'status' | 'measured' | 'threshold'>): string {
  if (check.status === 'error') return 'could not run'
  if (check.measured === null) return check.status === 'pass' ? 'within what the check allows' : 'no measure'
  return check.threshold === null ? `measured ${check.measured}` : `measured ${check.measured}, allowed ${check.threshold}`
}

/** What a block is and how it ends: it ends when the check passes, or when an administrator releases it on the server that set it. */
export function blockLine(check: Pick<QualityCheck, 'blocking' | 'blocked_since'> & Partial<Pick<QualityCheck, 'released_since' | 'released_by'>>): string | null {
  if (!check.blocking && check.released_since) return `Released by ${check.released_by ?? 'an administrator'} on ${check.released_since}: the check still fails, but what depends on the dataset is not held for it. The release ends when the check passes.`
  if (!check.blocking) return null
  return `Blocking dependents since ${check.blocked_since ?? 'an unknown time'}. It clears when the check passes; fix the data, then evaluate again.`
}

/** Why a block cannot be released from here, or null when it can: the server must offer it, and only an administrator releases. */
export function releaseBlockedReason(server: QualityServer | undefined, isAdmin: boolean): string | null {
  if (!server) return 'The server that evaluates this check is not paired any more.'
  if (server.error) return `${server.server} did not answer: ${server.error}`
  if (!server.can_release) return `${server.server} has no release of a block: it ends when the check passes.`
  if (!isAdmin) return 'Releasing a block lets what depends on the dataset run on data that fails the check, so it needs the admin role.'
  return null
}

/** Why a check cannot be evaluated from here, or null when it can. */
export function runBlockedReason(server: QualityServer | undefined, isAdmin: boolean): string | null {
  if (!server) return 'The server that evaluates this check is not paired any more.'
  if (server.error) return `${server.server} did not answer: ${server.error}`
  if (!server.can_run) return server.run_note ?? `${server.server} cannot evaluate on request.`
  if (!isAdmin) return 'Evaluating opens connections to the data, so it needs the admin role here (the server checks its own role too).'
  return null
}

/** The checks of one dataset as a lineage node's panel shows them: the same counts, in a line. */
export function panelLine(dataset: QualityDataset | undefined): string {
  if (!dataset) return 'No check is stored for this dataset.'
  const failing = dataset.checks.filter((c) => c.status === 'fail').length
  const blocking = dataset.blocking
  const parts = [`${dataset.checks.length} check${dataset.checks.length === 1 ? '' : 's'}`]
  if (blocking > 0) parts.push(`${blocking} blocking`)
  if (failing > 0) parts.push(`${failing} failing`)
  if (blocking === 0 && failing === 0) parts.push(dataset.checks.some((c) => c.status === 'error') ? 'some could not run' : 'all passing')
  return parts.join(', ')
}

const relay = (serverId: string, path: string) => `/relay/${encodeURIComponent(serverId)}${path}`

/** The history of a check is the evaluating server's own route (`/api/v1/quality/...` of its management API). */
export const historyPath = '/api/v1/quality/history'

/** The server's own route that releases a block (an administrator; audited there). */
export const releasePath = '/api/v1/quality/release'

/** The server's own route that evaluates a dataset's checks on request. */
export const runPath = '/api/v1/quality/run'

/** Where the alerts of failed checks go (`GET /api/quality/alerts`, the section `quality: alerts:` of settings.yaml). */
export interface QualityAlertsState { configured: boolean; channel: string | null; channelKnown: boolean; problem: string | null; recipients: string[] }

/** The words for the alerts card: what is set, and what is wrong with it. */
export function alertsLine(a: QualityAlertsState): string {
  if (a.problem) return `The alert settings cannot be used: ${a.problem}`
  if (!a.configured) return 'Alerts of failed checks are off. Name a delivery channel in settings.yaml (quality: alerts: channel:) and a message is sent when a check fails and when it passes again.'
  const who = a.recipients.length > 0 ? ` to ${a.recipients.join(', ')}` : ''
  return a.channelKnown ? `A failed check is sent through the channel ${a.channel}${who}, once when it fails and once when it passes again.` : `Alerts name the channel ${a.channel}, which this install does not have: nothing is sent. Add it on the Delivery page.`
}

export const qualityApi = {
  overview: () => request<QualityAnswer>('/api/quality'),
  alerts: () => request<QualityAlertsState>('/api/quality/alerts'),
  history: (check: Pick<QualityCheck, 'server_id' | 'check_id'>, dataset: string) =>
    request<{ results: QualityResult[] }>(relay(check.server_id, `${historyPath}?dataset=${encodeURIComponent(dataset)}&check=${encodeURIComponent(check.check_id)}`)),
  /** Releases the block a check set, on the server that owns the check (an administrator; the server audits it). */
  release: (serverId: string, dataset: string, check: string) => request<{ released: boolean }>(relay(serverId, releasePath), { method: 'POST', body: JSON.stringify({ dataset, check }) }),
  /** Evaluates the dataset's checks now, on the server that owns them (the server answers 403 for anyone but an administrator). */
  run: (serverId: string, dataset: string) => request<QualityRunAnswer>(relay(serverId, runPath), { method: 'POST', body: JSON.stringify({ dataset }) }),
}
