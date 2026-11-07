import { request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'

/** A replication, a scheduled plan or a notice of a Proxy, as one line of the view across servers. */
export interface MonitorRow {
  server: string
  serverName: string
  /** Unique among the rows: the server and what the row is about (for a notice, its number). */
  key: string
  product: 'dbdatasync' | 'dbdatabuild' | 'dbdataproxy' | 'dbdatarunner'
  kind: 'replication' | 'plan' | 'event' | 'task' | 'instance'
  subject: string
  /** What to look at first: a failed last run, a lag past the limit, a notice from a Proxy, a hold, a run going, or nothing. */
  state: 'failed' | 'gone' | 'lagging' | 'event' | 'paused' | 'running' | 'ok'
  detail: string
  /** Whether the person (or the server) holds it: a failed row can be held too, and then it is the hold that is released. */
  held: boolean
  /** What the row's buttons act on. */
  target: { replication: string } | { project: string; connection: string; plan: string } | { runnerTask: { package: string; channel: string; task: string } } | null
}

/** A replication whose slowest mapping is this far behind is shown as lagging. */
export const LAG_LIMIT_MS = 15 * 60 * 1000

const ORDER: Record<MonitorRow['state'], number> = { failed: 0, gone: 1, lagging: 2, event: 3, paused: 4, running: 5, ok: 6 }

/** Failures first, then a runner that is gone, then lag, then a Proxy's notices, then holds; within a state, by server and name (a Proxy's notices keep the order they came in, newest first). */
export const sortRows = (rows: MonitorRow[]) =>
  [...rows].sort((a, b) => ORDER[a.state] - ORDER[b.state] || a.serverName.localeCompare(b.serverName) || (a.state === 'event' ? 0 : a.subject.localeCompare(b.subject)))

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
  return { key: `${server.id}/${name}`, server: server.id, serverName: server.name, product: 'dbdatasync', kind: 'replication', subject: name, state, detail, held: status.paused, target: { replication: name } }
}

export interface BuildJob { connection: string; plan: string; schedule: string; next_due: string | null; last_started: string | null; last_exit: number | null; paused?: boolean; paused_by?: string | null }

/** One scheduled Build plan. */
export function buildRow(server: InstanceSummary, project: string, job: BuildJob): MonitorRow {
  let state: MonitorRow['state'] = 'ok'
  let detail = job.next_due ? `Next ${new Date(job.next_due).toLocaleString()}` : 'No next time'
  if (job.last_exit !== null && job.last_exit !== 0) { state = 'failed'; detail = `The last run ended with exit code ${job.last_exit}` }
  else if (job.paused) { state = 'paused'; detail = job.paused_by ? `Paused by ${job.paused_by}` : 'Paused' }
  return { key: `${server.id}/${project}/${job.connection}/${job.plan}`, server: server.id, serverName: server.name, product: 'dbdatabuild', kind: 'plan', subject: `${project}: ${job.connection}/${job.plan}`, state, detail, held: job.paused === true, target: { project, connection: job.connection, plan: job.plan } }
}

/** What a Proxy's notification feed says: `GET /api/v1/notifications` (a failed-login lockout, a refused statement, a limit reached). */
export interface ProxyNotification { id: number; kind: string; created: string; message: string }

/** Notices older than this are history, not something to look at now. */
export const NOTICE_WINDOW_MS = 24 * 60 * 60 * 1000

/** The Proxy's own words for its notification kinds. A kind this view does not know is shown as it came. */
const noticeTitle: Record<string, string> = { LoginLockedOut: 'Login locked out', StatementRefused: 'Statement refused', LimitReached: 'Limit reached' }

/** One notice of a Proxy as a row. It has no buttons: a notice is something that happened, not something that runs. */
export function proxyRow(server: InstanceSummary, notice: ProxyNotification): MonitorRow {
  return { key: `${server.id}/notice/${notice.id}`, server: server.id, serverName: server.name, product: 'dbdataproxy', kind: 'event', subject: noticeTitle[notice.kind] ?? notice.kind, state: 'event', detail: notice.message, held: false, target: null }
}

/** What a runner says in `GET /api/v1/status` about a task, and in `GET /api/v1/instances?all=true` about an instance. */
export interface RunnerTask {
  package: string; channel: string; task: string; paused: boolean; paused_by?: string | null; running: number | boolean; queued?: number | boolean
  problem?: string | null; waiting_reason?: string | null; last_run: { outcome: string; started?: string } | null
}
export interface RunnerInstanceRow { id: string; host: string; live?: boolean; state?: string; heartbeat?: string }

const runnerBad = (outcome: string) => outcome === 'failed' || outcome === 'completedwithfailures' || outcome === 'interrupted'

/** One task of a runner. A last run that failed outranks everything else; a task that waits (a label no runner has) or has a problem is shown as failed too, since nothing will run it until someone acts. */
export function runnerTaskRow(server: InstanceSummary, task: RunnerTask): MonitorRow {
  const ref = { package: task.package, channel: task.channel, task: task.task }
  const key = `${task.package}/${task.channel}/${task.task}`
  let state: MonitorRow['state'] = 'ok'
  let detail = task.last_run ? `Last run ${task.last_run.outcome}` : 'No run yet'
  if (task.last_run && runnerBad(task.last_run.outcome)) { state = 'failed'; detail = `The last run ${task.last_run.outcome === 'completedwithfailures' ? 'completed with failures' : task.last_run.outcome}` }
  else if (task.waiting_reason) { state = 'failed'; detail = task.waiting_reason }
  else if (task.problem) { state = 'failed'; detail = task.problem }
  else if (task.paused) { state = 'paused'; detail = task.paused_by ? `Paused by ${task.paused_by}` : 'Paused' }
  else if (task.running === true || (typeof task.running === 'number' && task.running > 0)) { state = 'running'; detail = 'Running now' }
  return { key: `${server.id}/${key}`, server: server.id, serverName: server.name, product: 'dbdatarunner', kind: 'task', subject: key, state, detail, held: task.paused, target: { runnerTask: ref } }
}

/** A runner of the group that is not live: it left (marked gone) or went silent past its lease. It has no buttons; what to do is the administrator's. */
export function runnerGoneRow(server: InstanceSummary, instance: RunnerInstanceRow): MonitorRow {
  return { key: `${server.id}/instance/${instance.id}`, server: server.id, serverName: server.name, product: 'dbdatarunner', kind: 'instance', subject: `runner on ${instance.host}`, state: 'gone', detail: instance.state === 'Gone' ? 'The runner stopped' : 'The runner stopped reporting', held: false, target: null }
}

const relay = (server: InstanceSummary, path: string) => `/relay/${server.id}${path}`
const enc = encodeURIComponent

/** Everything the view shows, from every reachable server of a kind it knows. A server that does not answer is reported beside the rows, never as a row, and never stops the others. */
export async function loadRows(servers: InstanceSummary[], now: number = Date.now()): Promise<{ rows: MonitorRow[]; problems: { server: string; message: string }[] }> {
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
      } else if (server.product === 'dbdataproxy') {
        const { notifications } = await request<{ notifications: ProxyNotification[] }>(relay(server, '/api/v1/notifications'))
        // the feed lists the oldest first; the newest are what matters here
        rows.push(...notifications.filter((n) => now - Date.parse(n.created) <= NOTICE_WINDOW_MS).reverse().slice(0, 20).map((n) => proxyRow(server, n)))
      } else if (server.product === 'dbdatarunner') {
        const [status, group] = await Promise.all([
          request<{ tasks: RunnerTask[] }>(relay(server, '/api/v1/status')),
          request<{ instances: RunnerInstanceRow[] }>(relay(server, '/api/v1/instances?all=true')),
        ])
        rows.push(...status.tasks.map((t) => runnerTaskRow(server, t)), ...group.instances.filter((i) => i.live === false).map((i) => runnerGoneRow(server, i)))
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
  if (row.target === null) return Promise.resolve(null)   // a notice has nothing to hold or run
  const base = `/relay/${row.server}`
  if ('replication' in row.target) {
    const name = enc(row.target.replication)
    return what === 'run'
      ? request<unknown>(`${base}/api/replications/${name}/runs`, { method: 'POST' })
      : request<unknown>(`${base}/api/replications/${name}/paused`, { method: 'PUT', body: JSON.stringify({ paused: what === 'pause', note: null }) })
  }
  if ('runnerTask' in row.target) {
    const t = row.target.runnerTask
    return request<unknown>(`${base}/api/v1/tasks/${enc(t.package)}/${enc(t.channel)}/${enc(t.task)}/${what}`, { method: 'POST', body: what === 'run' ? '{}' : undefined })
  }
  const { project, connection, plan } = row.target
  return request<unknown>(`${base}/api/v1/projects/${enc(project)}/schedule/${enc(connection)}/${enc(plan)}/${what}`, { method: 'POST' })
}
