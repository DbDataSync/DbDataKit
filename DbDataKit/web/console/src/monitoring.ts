import { request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'

/** A replication or a scheduled plan, as one line of the view across servers. */
export interface MonitorRow {
  server: string
  serverName: string
  product: 'dbdatasync' | 'dbdatabuild'
  kind: 'replication' | 'plan'
  subject: string
  /** What to look at first: a failed last run, a lag past the limit, a hold, a run going, or nothing. */
  state: 'failed' | 'lagging' | 'paused' | 'running' | 'ok'
  detail: string
  /** What the row's buttons act on. */
  target: { replication: string } | { project: string; connection: string; plan: string }
}

/** A replication whose slowest mapping is this far behind is shown as lagging. */
export const LAG_LIMIT_MS = 15 * 60 * 1000

const ORDER: Record<MonitorRow['state'], number> = { failed: 0, lagging: 1, paused: 2, running: 3, ok: 4 }

/** Failures first, then lag, then holds; within a state, by server and name. */
export const sortRows = (rows: MonitorRow[]) =>
  [...rows].sort((a, b) => ORDER[a.state] - ORDER[b.state] || a.serverName.localeCompare(b.serverName) || a.subject.localeCompare(b.subject))

export function duration(ms: number): string {
  const minutes = Math.round(ms / 60000)
  if (minutes < 1) return 'under a minute'
  if (minutes < 120) return `${minutes} min`
  const hours = Math.round(minutes / 60)
  return hours < 48 ? `${hours} h` : `${Math.round(hours / 24)} days`
}

interface SyncStatus { running: boolean; enabled: boolean; paused: boolean; pauseNote?: string | null }
interface SyncLag { highestLagMs: number | null }
interface SyncRuns { runs: { status: string; startedAtUtc?: string | null }[] }

/** One Sync replication from what its server says about it. A failed last run outranks lag, and lag outranks a hold, since the hold is the person's own doing. */
export function syncRow(server: InstanceSummary, name: string, status: SyncStatus, lag: SyncLag, runs: SyncRuns): MonitorRow {
  const last = runs.runs[0]
  const highest = lag.highestLagMs
  let state: MonitorRow['state'] = 'ok'
  let detail = highest === null ? 'No lag figure yet' : `${duration(highest)} behind`
  if (last?.status === 'Failed') { state = 'failed'; detail = 'The last run failed' }
  else if (highest !== null && highest > LAG_LIMIT_MS) { state = 'lagging'; detail = `${duration(highest)} behind` }
  else if (status.paused) { state = 'paused'; detail = status.pauseNote ? `Paused: ${status.pauseNote}` : 'Paused' }
  else if (!status.enabled) { state = 'paused'; detail = 'Switched off' }
  else if (status.running) { state = 'running'; detail = 'Running now' }
  return { server: server.id, serverName: server.name, product: 'dbdatasync', kind: 'replication', subject: name, state, detail, target: { replication: name } }
}

export interface BuildJob { connection: string; plan: string; schedule: string; next_due: string | null; last_started: string | null; last_exit: number | null; paused?: boolean; paused_by?: string | null }

/** One scheduled Build plan. */
export function buildRow(server: InstanceSummary, project: string, job: BuildJob): MonitorRow {
  let state: MonitorRow['state'] = 'ok'
  let detail = job.next_due ? `Next ${new Date(job.next_due).toLocaleString()}` : 'No next time'
  if (job.last_exit !== null && job.last_exit !== 0) { state = 'failed'; detail = `The last run ended with exit code ${job.last_exit}` }
  else if (job.paused) { state = 'paused'; detail = job.paused_by ? `Paused by ${job.paused_by}` : 'Paused' }
  return { server: server.id, serverName: server.name, product: 'dbdatabuild', kind: 'plan', subject: `${project}: ${job.connection}/${job.plan}`, state, detail, target: { project, connection: job.connection, plan: job.plan } }
}

const relay = (server: InstanceSummary, path: string) => `/relay/${server.id}${path}`
const enc = encodeURIComponent

/** Everything the view shows, from every reachable server of a kind it knows. A server that does not answer is reported beside the rows, never as a row, and never stops the others. */
export async function loadRows(servers: InstanceSummary[]): Promise<{ rows: MonitorRow[]; problems: { server: string; message: string }[] }> {
  const rows: MonitorRow[] = []
  const problems: { server: string; message: string }[] = []
  await Promise.all(servers.filter((s) => s.reachable).map(async (server) => {
    try {
      if (server.product === 'dbdatasync') {
        const names = await request<string[]>(relay(server, '/api/replications'))
        const found = await Promise.all(names.map(async (name) => {
          const [status, lag, runs] = await Promise.all([
            request<SyncStatus>(relay(server, `/api/replications/${enc(name)}/status`)),
            request<SyncLag>(relay(server, `/api/replications/${enc(name)}/lag`)),
            request<SyncRuns>(relay(server, `/api/replications/${enc(name)}/runs?limit=1`)),
          ])
          return syncRow(server, name, status, lag, runs)
        }))
        rows.push(...found)
      } else if (server.product === 'dbdatabuild') {
        const { projects } = await request<{ projects: { id: string }[] }>(relay(server, '/api/v1/projects'))
        for (const project of projects) {
          const { jobs } = await request<{ jobs: BuildJob[] }>(relay(server, `/api/v1/projects/${enc(project.id)}/schedule`))
          rows.push(...jobs.map((job) => buildRow(server, project.id, job)))
        }
      }
    } catch (error) {
      problems.push({ server: server.name, message: error instanceof Error ? error.message : 'No answer' })
    }
  }))
  return { rows: sortRows(rows), problems }
}

/** The buttons of a row: hold or release it, and run it now. Each is one call to the server the row came from; the server decides whether this person may. */
export const actions = {
  pause: (row: MonitorRow) => act(row, 'pause'),
  resume: (row: MonitorRow) => act(row, 'resume'),
  run: (row: MonitorRow) => act(row, 'run'),
}

function act(row: MonitorRow, what: 'pause' | 'resume' | 'run') {
  const base = `/relay/${row.server}`
  if ('replication' in row.target) {
    const name = enc(row.target.replication)
    return what === 'run'
      ? request<unknown>(`${base}/api/replications/${name}/runs`, { method: 'POST' })
      : request<unknown>(`${base}/api/replications/${name}/paused`, { method: 'PUT', body: JSON.stringify({ paused: what === 'pause', note: null }) })
  }
  const { project, connection, plan } = row.target
  return request<unknown>(`${base}/api/v1/projects/${enc(project)}/schedule/${enc(connection)}/${enc(plan)}/${what}`, { method: 'POST' })
}
