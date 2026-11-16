import { request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'

/** A runner's task, a notice of a Gateway, a certificate or a server that is down, as one line of the view across servers. */
export interface MonitorRow {
  server: string
  serverName: string
  /** Unique among the rows: the server and what the row is about (for a notice, its number). */
  key: string
  product: 'dbdatagateway' | 'dbdatarunner' | 'dbdataworkspace' | 'dbdatakit' | 'unknown'
  kind: 'event' | 'task' | 'instance' | 'certificate' | 'server'
  subject: string
  /** What to look at first: a failed last run, a runner that is gone, a certificate near its end, a notice from a Gateway, a hold, a run going, or nothing. */
  state: 'failed' | 'gone' | 'expiring' | 'event' | 'paused' | 'running' | 'ok'
  detail: string
  /** Whether the person (or the server) holds it: a failed row can be held too, and then it is the hold that is released. */
  held: boolean
  /** Who schedules a runner's task, as the runner says: `runner`, or `old`/`observe` for a task whose publishing product kept its own schedule. Absent where the row has no such thing. */
  owner?: 'old' | 'observe' | 'runner'
  /** What the row's buttons act on. */
  target: { runnerTask: { package: string; channel: string; task: string } } | null
}

const ORDER: Record<MonitorRow['state'], number> = { failed: 0, gone: 1, expiring: 2, event: 3, paused: 4, running: 5, ok: 6 }

/** Failures first, then a runner that is gone, then a certificate near its end, then a Gateway's notices, then holds; within a state, by server and name (a Gateway's notices keep the order they came in, newest first). */
export const sortRows = (rows: MonitorRow[]) =>
  [...rows].sort((a, b) => ORDER[a.state] - ORDER[b.state] || a.serverName.localeCompare(b.serverName) || (a.state === 'event' ? 0 : a.subject.localeCompare(b.subject)))

export function duration(ms: number): string {
  const minutes = Math.round(ms / 60000)
  if (minutes < 1) return 'under a minute'
  if (minutes < 120) return `${minutes} min`
  const hours = Math.round(minutes / 60)
  return hours < 48 ? `${hours} h` : `${Math.round(hours / 24)} days`
}

const ownerOf = (owner: string | undefined): 'old' | 'observe' | 'runner' => (owner === 'observe' || owner === 'runner' ? owner : 'old')

/** What a Gateway's notification feed says: `GET /api/v1/notifications` (a failed-login lockout, a refused statement, a limit reached). */
export interface GatewayNotification { id: number; kind: string; created: string; message: string }

/** Notices older than this are history, not something to look at now. */
export const NOTICE_WINDOW_MS = 24 * 60 * 60 * 1000

/** The Gateway's own words for its notification kinds. A kind this view does not know is shown as it came. */
const noticeTitle: Record<string, string> = { LoginLockedOut: 'Login locked out', StatementRefused: 'Statement refused', LimitReached: 'Limit reached' }

/** One notice of a Gateway as a row. It has no buttons: a notice is something that happened, not something that runs. */
export function gatewayRow(server: InstanceSummary, notice: GatewayNotification): MonitorRow {
  return { key: `${server.id}/notice/${notice.id}`, server: server.id, serverName: server.name, product: productOf(server.product), kind: 'event', subject: noticeTitle[notice.kind] ?? notice.kind, state: 'event', detail: notice.message, held: false, target: null }
}

/** What a server says in `GET /api/v1/certificate` (UI10): the figures the certificate screens show, read here so that an expiry is seen before a login fails. */
export interface GatewayCertificate { present: boolean; subject?: string; days_left?: number; needs_renewal?: boolean; renew_before_days: number; warning?: string }

function certificateLine(subject: string, days: number): { state: 'failed' | 'expiring'; detail: string } {
  if (days < 0) return { state: 'failed', detail: `The certificate for ${subject} expired ${-days} day${days === -1 ? '' : 's'} ago` }
  return { state: 'expiring', detail: `The certificate for ${subject} ends in ${days} day${days === 1 ? '' : 's'}` }
}

const products = ['dbdatagateway', 'dbdatarunner', 'dbdataworkspace', 'dbdatakit'] as const
const productOf = (product: string | null): MonitorRow['product'] => products.find((k) => k === product) ?? 'unknown'

/** The console's own certificate as a server of its own for the rows: `GET /api/v1/certificate` of the console, not through the relay (UI10). */
export const consoleServer: InstanceSummary = { id: 'console', name: 'This console', url: '', product: 'dbdatakit', version: null, apiVersion: null, reachable: true, ready: true, problem: null, pairedAt: null }

/** A certificate of a server that is expired (a failure) or within its warning window (expiring); null when it is fine, when the server has none, or when it cannot say. A valid certificate is no row. It has no buttons: renewing is on the server's own certificate screen. */
export function certificateRow(server: InstanceSummary, cert: GatewayCertificate): MonitorRow | null {
  if (!cert.present || cert.days_left === undefined) return null
  const subject = (cert.subject ?? server.name).replace(/^CN=/i, ''), days = cert.days_left
  if (cert.needs_renewal !== true && days > cert.renew_before_days) return null
  const { state, detail } = certificateLine(subject, days)
  return { key: `${server.id}/certificate`, server: server.id, serverName: server.name, product: productOf(server.product), kind: 'certificate', subject: 'Certificate', state, detail, held: false, target: null }
}

/** What a runner says in `GET /api/v1/status` about a task, and in `GET /api/v1/instances?all=true` about an instance. */
export interface RunnerTask {
  package: string; channel: string; task: string; paused: boolean; paused_by?: string | null; running: number | boolean; queued?: number | boolean
  problem?: string | null; waiting_reason?: string | null; owner?: string; last_run: { outcome: string; started?: string } | null
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
  const owner = ownerOf(task.owner === undefined ? 'runner' : task.owner)
  // a task the runner does not schedule is the publishing product's to run: say so beside what is known of it
  if (owner === 'old') detail = `${detail}. Scheduled by the publishing product, not by this runner`
  else if (owner === 'observe') detail = `${detail}. Scheduled by the publishing product, observed by this runner`
  return { owner, key: `${server.id}/${key}`, server: server.id, serverName: server.name, product: 'dbdatarunner', kind: 'task', subject: key, state, detail, held: task.paused, target: { runnerTask: ref } }
}

/** A runner of the group that is not live: it left (marked gone) or went silent past its lease. It has no buttons; what to do is the administrator's. */
export function runnerGoneRow(server: InstanceSummary, instance: RunnerInstanceRow): MonitorRow {
  return { key: `${server.id}/instance/${instance.id}`, server: server.id, serverName: server.name, product: 'dbdatarunner', kind: 'instance', subject: `runner on ${instance.host}`, state: 'gone', detail: instance.state === 'Gone' ? 'The runner stopped' : 'The runner stopped reporting', held: false, target: null }
}

/** A server of any kind that does not answer, or answers that it is not ready: a failure, since none of its screens can be read or acted on until someone looks. The servers that answer are read for their own rows. It has no buttons: the Servers page is where it is looked at. */
export function serverRow(server: InstanceSummary): MonitorRow | null {
  const known = ['dbdatagateway', 'dbdatarunner', 'dbdataworkspace'] as const
  const product = known.find((k) => k === server.product) ?? 'unknown'
  const detail = !server.reachable ? `The server does not answer: ${server.problem ?? 'no answer'}` : server.ready === false ? `The server answers that it is not ready${server.problem ? `: ${server.problem}` : ''}` : null
  return detail === null ? null : { key: `${server.id}/server`, server: server.id, serverName: server.name, product, kind: 'server', subject: 'Server', state: 'failed', detail, held: false, target: null }
}

const relay = (server: InstanceSummary, path: string) => `/relay/${server.id}${path}`
const enc = encodeURIComponent

/** A server's certificate row, from the route every server has (`GET /api/v1/certificate`, UI10); a server that has no such route (an older one) or cannot say shows none, and its other rows still show. */
async function certificateOf(server: InstanceSummary, rows: MonitorRow[]) {
  const row = await request<GatewayCertificate>(relay(server, '/api/v1/certificate')).then((c) => certificateRow(server, c), () => null)
  if (row) rows.push(row)
}

/** Everything the view shows, from every reachable server of a kind it knows. A server that does not answer is reported beside the rows, never as a row, and never stops the others. */
export async function loadRows(servers: InstanceSummary[], now: number = Date.now(), withConsole = false): Promise<{ rows: MonitorRow[]; problems: { server: string; message: string }[] }> {
  const rows: MonitorRow[] = []
  const problems: { server: string; message: string }[] = []
  // the certificate of the console itself (UI10): a console that cannot say shows no row for it
  if (withConsole) {
    const own = await request<GatewayCertificate>('/api/v1/certificate').then((c) => certificateRow(consoleServer, c), () => null)
    if (own) rows.push(own)
  }
  rows.push(...servers.map(serverRow).filter((r): r is MonitorRow => r !== null))
  await Promise.all(servers.filter((s) => s.reachable).map(async (server) => {
    try {
      if (server.product === 'dbdatagateway') {
        const cert = await request<GatewayCertificate>(relay(server, '/api/v1/certificate')).then((c) => certificateRow(server, c), () => null)
        if (cert) rows.push(cert)
        const { notifications } = await request<{ notifications: GatewayNotification[] }>(relay(server, '/api/v1/notifications'))
        // the feed lists the oldest first; the newest are what matters here
        rows.push(...notifications.filter((n) => now - Date.parse(n.created) <= NOTICE_WINDOW_MS).reverse().slice(0, 20).map((n) => gatewayRow(server, n)))
      } else if (server.product === 'dbdatarunner') {
        await certificateOf(server, rows)
        const [status, group] = await Promise.all([
          request<{ tasks: RunnerTask[] }>(relay(server, '/api/v1/status')),
          request<{ instances: RunnerInstanceRow[] }>(relay(server, '/api/v1/instances?all=true')),
        ])
        rows.push(...status.tasks.map((t) => runnerTaskRow(server, t)), ...group.instances.filter((i) => i.live === false).map((i) => runnerGoneRow(server, i)))
      } else if (server.product === 'dbdataworkspace') {
        await certificateOf(server, rows)
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
  const t = row.target.runnerTask
  return request<unknown>(`/relay/${row.server}/api/v1/tasks/${enc(t.package)}/${enc(t.channel)}/${enc(t.task)}/${what}`, { method: 'POST', body: what === 'run' ? '{}' : undefined })
}
