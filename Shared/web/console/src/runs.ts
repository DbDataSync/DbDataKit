import { request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'

/** One run of a runner's task, as one line of the history across runners. */
export interface RunRow {
  /** Unique among the rows: the server and the run. */
  key: string
  server: string
  serverName: string
  product: 'dbdatarunner'
  /** What ran: `<package>/<channel>/<task>`. */
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

/** How many recent runs are asked of each runner (ten times this): the page is the newest of every runner, not an archive (a runner's own Runs page holds the full history). */
export const PER_SOURCE = 10

const relay = (server: InstanceSummary, path: string) => `/relay/${server.id}${path}`

/** The recent runs of every reachable runner. A server that does not answer is reported beside the rows, never as a row, and never stops the others. */
export async function loadRuns(servers: InstanceSummary[]): Promise<{ rows: RunRow[]; problems: { server: string; message: string }[] }> {
  const rows: RunRow[] = []
  const problems: { server: string; message: string }[] = []
  await Promise.all(servers.filter((s) => s.reachable && s.product === 'dbdatarunner').map(async (server) => {
    try {
      const { runs } = await request<{ runs: RunnerRun[] }>(relay(server, `/api/v1/runs?limit=${PER_SOURCE * 10}`))
      rows.push(...runs.map((r) => runnerRunRow(server, r)))
    } catch (error) {
      problems.push({ server: server.name, message: error instanceof Error ? error.message : 'No answer' })
    }
  }))
  return { rows: sortRuns(rows), problems }
}
