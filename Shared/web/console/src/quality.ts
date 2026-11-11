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
  server: string
  server_id: string
  product: string | null
}

export interface QualityDataset { dataset: string; blocking: number; checks: QualityCheck[] }

/** What one server said when asked: whether it can evaluate on request, and when it cannot, how its checks are evaluated. */
export interface QualityServer { server: string; server_id: string; product: string | null; error: string | null; checks: number | null; can_run: boolean; run_note: string | null }

export interface QualityAnswer { datasets: QualityDataset[]; servers: QualityServer[] }

export interface QualityResult { id: string; status: QualityCheck['status']; evaluated_at: string; measured: number | null; threshold: number | null; sample_keys: string[]; run_id: string | null; message: string | null }

export interface QualityRunAnswer { replication?: string; dataset: string; evaluated: number; passed: number; failed: number; errors: number; blocking: number; notes: string[] }

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

/** What a block is and how it ends. There is no release by hand yet for a block of Build or Sync: it ends when the check passes; the serving layer's administrator release is plan 23a FQ15. */
export function blockLine(check: Pick<QualityCheck, 'blocking' | 'blocked_since'>): string | null {
  if (!check.blocking) return null
  return `Blocking dependents since ${check.blocked_since ?? 'an unknown time'}. It clears when the check passes; fix the data, then evaluate again.`
}

/** Why a check cannot be evaluated from here, or null when it can. */
export function runBlockedReason(server: QualityServer | undefined, isAdmin: boolean): string | null {
  if (!server) return 'The server that evaluates this check is not paired any more.'
  if (server.error) return `${server.server} did not answer: ${server.error}`
  if (!server.can_run) return server.run_note ?? `${server.server} cannot evaluate on request.`
  if (!isAdmin) return 'Evaluating opens connections to the data, so it needs the admin role.'
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

/** The history of a check is the evaluating server's own route; the path is the one its product serves. */
export const historyPath = (product: string | null) => (product === 'dbdatabuild' ? '/api/v1/quality/history' : '/api/quality/history')

export const qualityApi = {
  overview: () => request<QualityAnswer>('/api/quality'),
  history: (check: Pick<QualityCheck, 'server_id' | 'product' | 'check_id'>, dataset: string) =>
    request<{ results: QualityResult[] }>(relay(check.server_id, `${historyPath(check.product)}?dataset=${encodeURIComponent(dataset)}&check=${encodeURIComponent(check.check_id)}`)),
  /** Evaluates the dataset's checks now, on the server that owns them (a Sync server; the server answers 403 for anyone but an administrator). */
  run: (serverId: string, dataset: string) => request<QualityRunAnswer>(relay(serverId, '/api/quality/run'), { method: 'POST', body: JSON.stringify({ dataset }) }),
}
