import { request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'

/** One run of a Sync replication or of a runner's task, as one line of the history across servers. */
export interface RunRow {
  /** Unique among the rows: the server and the run. */
  key: string
  server: string
  serverName: string
  product: 'dbdatasync' | 'dbdatarunner'
  /** What ran: `<replication>` (and the mapping or segment) for Sync, `<package>/<channel>/<task>` for a runner. */
  subject: string
  state: 'failed' | 'running' | 'queued' | 'stopped' | 'succeeded'
  /** The failure in a sentence, or what the run did (rows written, items processed); empty when nothing is known. */
  detail: string
  /** When the work was queued, started and ended (ISO); null when it has not happened or is not known. The row's time is the first of these that is known. */
  queued: string | null
  started: string | null
  ended: string | null
  /** The console route of the screen that shows the run and what can be done about it. */
  link: string
}

/** A Sync run, as `GET /api/replications/{name}/runs` lists it (`TaskRunRecord`), as far as this view reads it. */
export interface SyncRun {
  runId: string; taskName: string; status: 'Queued' | 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'; runKind: string; mappingName: string; segmentLabel: string | null
  enqueuedAtUtc: string | null; startedAtUtc: string | null; endedAtUtc: string | null; rowsRead: number; rowsWritten: number; errorSummary: string | null
}

const syncState: Record<SyncRun['status'], RunRow['state']> = { Queued: 'queued', Pending: 'queued', Running: 'running', Succeeded: 'succeeded', Failed: 'failed', Cancelled: 'stopped' }

/** One run of a Sync replication. A bulk load or a reconcile says so, since it is not the ordinary pass. */
export function syncRunRow(server: InstanceSummary, replication: string, run: SyncRun): RunRow {
  const state = syncState[run.status] ?? 'queued'
  const kind = run.runKind === 'BulkLoad' ? 'bulk load' : run.runKind === 'ReconcileDeletes' ? 'reconcile' : ''
  const part = [run.mappingName, run.segmentLabel].filter((p) => p && p !== '').join(' ')
  const subject = [replication, part, kind && `(${kind})`].filter((p) => p !== '' && p !== null).join(' ')
  const detail = state === 'failed' ? (run.errorSummary ?? 'The run failed') : state === 'succeeded' ? `${run.rowsRead} read, ${run.rowsWritten} written` : run.errorSummary ?? ''
  return { key: `${server.id}/sync/${run.runId}`, server: server.id, serverName: server.name, product: 'dbdatasync', subject, state, detail, queued: run.enqueuedAtUtc, started: run.startedAtUtc, ended: run.endedAtUtc, link: `/servers/${server.id}/replications/${encodeURIComponent(replication)}/monitoring/history` }
}

/** A runner's run, as `GET /api/v1/runs` lists it, as far as this view reads it. */
export interface RunnerRun {
  id: string; package: string; channel: string; task: string; outcome: string; started: string; finished: string | null; rows: number | null; failed_item: string | null; failure: { kind: string; message: string } | null
}

const runnerState = (outcome: string): RunRow['state'] =>
  outcome === 'running' ? 'running' : outcome === 'succeeded' ? 'succeeded' : outcome === 'stopped' ? 'stopped' : 'failed'   // failed, completedwithfailures and interrupted are all runs somebody should look at

/** One run of a runner's task. A run that completed with failures or was interrupted is shown as failed and says which. */
export function runnerRunRow(server: InstanceSummary, run: RunnerRun): RunRow {
  const state = runnerState(run.outcome)
  let detail = ''
  if (run.failure) detail = `${run.failure.kind}: ${run.failure.message}`
  else if (run.outcome === 'completedwithfailures') detail = `Completed with failures${run.failed_item ? `, the first item was ${run.failed_item}` : ''}`
  else if (run.outcome === 'interrupted') detail = 'Interrupted: the runner stopped before it finished'
  else if (run.rows !== null && state === 'succeeded') detail = `${run.rows} item${run.rows === 1 ? '' : 's'} processed`
  return { key: `${server.id}/runner/${run.id}`, server: server.id, serverName: server.name, product: 'dbdatarunner', subject: `${run.package}/${run.channel}/${run.task}`, state, detail, queued: null, started: run.started, ended: run.finished, link: `/servers/${server.id}/runs` }
}

/** The moment a row is ordered by: when it started, else when it was queued; unknown last. */
export const timeOf = (row: RunRow): number => {
  const at = Date.parse(row.started ?? row.queued ?? '')
  return Number.isNaN(at) ? Number.NEGATIVE_INFINITY : at
}

/** Newest first; a run that is going is not older than one that has finished at the same instant. */
export const sortRuns = (rows: RunRow[]): RunRow[] => [...rows].sort((a, b) => timeOf(b) - timeOf(a) || a.serverName.localeCompare(b.serverName) || a.subject.localeCompare(b.subject))

/** How long a run took, in words; empty while it has not ended. */
export function took(row: Pick<RunRow, 'started' | 'queued' | 'ended'>): string {
  const from = Date.parse(row.started ?? row.queued ?? ''), to = Date.parse(row.ended ?? '')
  if (Number.isNaN(from) || Number.isNaN(to) || to < from) return ''
  const seconds = Math.round((to - from) / 1000)
  if (seconds < 60) return `${seconds} s`
  const minutes = Math.round(seconds / 60)
  return minutes < 120 ? `${minutes} min` : `${Math.round(minutes / 60)} h`
}

/** The runs the page shows: all of them, or only the ones that failed (what somebody has to look at). */
export const visible = (rows: RunRow[], onlyFailures: boolean): RunRow[] => (onlyFailures ? rows.filter((r) => r.state === 'failed') : rows)

/** How many recent runs are asked of each replication and of each runner: the page is the newest of every server, not an archive (a replication's own page holds the full history). */
export const PER_SOURCE = 10

const relay = (server: InstanceSummary, path: string) => `/relay/${server.id}${path}`
const enc = encodeURIComponent

/** The recent runs of every reachable Sync and runner server. A server that does not answer is reported beside the rows, never as a row, and never stops the others. */
export async function loadRuns(servers: InstanceSummary[]): Promise<{ rows: RunRow[]; problems: { server: string; message: string }[] }> {
  const rows: RunRow[] = []
  const problems: { server: string; message: string }[] = []
  await Promise.all(servers.filter((s) => s.reachable).map(async (server) => {
    try {
      if (server.product === 'dbdatasync') {
        const names = await request<string[]>(relay(server, '/api/replications'))
        const lists = await Promise.all(names.map(async (name) => ({ name, page: await request<{ runs: SyncRun[] }>(relay(server, `/api/replications/${enc(name)}/runs?limit=${PER_SOURCE}`)) })))
        for (const { name, page } of lists) rows.push(...page.runs.map((r) => syncRunRow(server, name, r)))
      } else if (server.product === 'dbdatarunner') {
        const { runs } = await request<{ runs: RunnerRun[] }>(relay(server, `/api/v1/runs?limit=${PER_SOURCE * 10}`))
        rows.push(...runs.map((r) => runnerRunRow(server, r)))
      }
    } catch (error) {
      problems.push({ server: server.name, message: error instanceof Error ? error.message : 'No answer' })
    }
  }))
  return { rows: sortRuns(rows), problems }
}
