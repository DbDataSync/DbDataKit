import { request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'

/** A replication, a scheduled plan or a notice of a Gateway, as one line of the view across servers. */
export interface MonitorRow {
  server: string
  serverName: string
  /** Unique among the rows: the server and what the row is about (for a notice, its number). */
  key: string
  product: 'dbdatasync' | 'dbdatabuild' | 'dbdatagateway' | 'dbdatarunner' | 'dbdatastore' | 'dbdataworkspace' | 'dbdatakit' | 'unknown'
  kind: 'replication' | 'plan' | 'event' | 'task' | 'instance' | 'certificate' | 'server'
  subject: string
  /** What to look at first: a failed last run, a lag past the limit, a notice from a Gateway, a hold, a run going, or nothing. */
  state: 'failed' | 'gone' | 'lagging' | 'expiring' | 'event' | 'paused' | 'running' | 'ok'
  detail: string
  /** Whether the person (or the server) holds it: a failed row can be held too, and then it is the hold that is released. */
  held: boolean
  /** Who schedules it, for a scheduled plan or a runner's task: `old` (the Build or Sync server that publishes it), `observe` or `runner`. Absent where the row has no such thing. */
  owner?: 'old' | 'observe' | 'runner'
  /** For a plan the runner schedules: which runner and channel, and why its own row could not be read. */
  runner?: { server: string; channel: string; error: string | null }
  /** What the row's buttons act on. */
  target: { replication: string } | { project: string; connection: string; plan: string } | { runnerTask: { package: string; channel: string; task: string } } | null
}

/** A replication whose slowest mapping is this far behind is shown as lagging. */
export const LAG_LIMIT_MS = 15 * 60 * 1000

const ORDER: Record<MonitorRow['state'], number> = { failed: 0, gone: 1, lagging: 2, expiring: 3, event: 4, paused: 5, running: 6, ok: 7 }

/** Failures first, then a runner that is gone, then lag, then a certificate near its end, then a Gateway's notices, then holds; within a state, by server and name (a Gateway's notices keep the order they came in, newest first). */
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

export interface BuildJob {
  connection: string; plan: string; schedule: string; next_due: string | null; last_started: string | null; last_exit: number | null; paused?: boolean; paused_by?: string | null
  /** Who schedules the plan (plan 28c E09) and, for the runner's, its own row. A server that predates ownership says nothing: its own scheduler does. */
  owner?: string
  runner?: { server: string; channel: string; error?: string | null; last_run?: { outcome: string } | null }
}

const ownerOf = (owner: string | undefined): 'old' | 'observe' | 'runner' => (owner === 'observe' || owner === 'runner' ? owner : 'old')

/** Who schedules a plan, as a sentence for a row. */
export function ownerSentence(owner: 'old' | 'observe' | 'runner', runner?: { server: string; channel: string }): string {
  if (owner === 'runner') return runner ? `Scheduled by the runner ${runner.server} (${runner.channel})` : 'Scheduled by the runner'
  if (owner === 'observe') return runner ? `Scheduled here, observed by the runner ${runner.server}` : 'Scheduled here, observed by a runner'
  return 'Scheduled by this server'
}

/** One scheduled Build plan. */
export function buildRow(server: InstanceSummary, project: string, job: BuildJob): MonitorRow {
  let state: MonitorRow['state'] = 'ok'
  let detail = job.next_due ? `Next ${new Date(job.next_due).toLocaleString()}` : 'No next time'
  if (job.last_exit !== null && job.last_exit !== 0) { state = 'failed'; detail = `The last run ended with exit code ${job.last_exit}` }
  else if (job.paused) { state = 'paused'; detail = job.paused_by ? `Paused by ${job.paused_by}` : 'Paused' }
  const owner = ownerOf(job.owner)
  if (owner !== 'old') detail = `${detail}. ${ownerSentence(owner, job.runner)}`
  if (job.runner?.error) detail = `${detail}. The runner did not answer: ${job.runner.error}`
  return { owner, runner: job.runner ? { server: job.runner.server, channel: job.runner.channel, error: job.runner.error ?? null } : undefined, key: `${server.id}/${project}/${job.connection}/${job.plan}`, server: server.id, serverName: server.name, product: 'dbdatabuild', kind: 'plan', subject: `${project}: ${job.connection}/${job.plan}`, state, detail, held: job.paused === true, target: { project, connection: job.connection, plan: job.plan } }
}

/** What a Gateway's notification feed says: `GET /api/v1/notifications` (a failed-login lockout, a refused statement, a limit reached). */
export interface GatewayNotification { id: number; kind: string; created: string; message: string }

/** Notices older than this are history, not something to look at now. */
export const NOTICE_WINDOW_MS = 24 * 60 * 60 * 1000

/** The Gateway's own words for its notification kinds. A kind this view does not know is shown as it came. */
const noticeTitle: Record<string, string> = { LoginLockedOut: 'Login locked out', StatementRefused: 'Statement refused', LimitReached: 'Limit reached' }

/** One notice of a Gateway as a row. It has no buttons: a notice is something that happened, not something that runs. */
export function gatewayRow(server: InstanceSummary, notice: GatewayNotification): MonitorRow {
  return { key: `${server.id}/notice/${notice.id}`, server: server.id, serverName: server.name, product: 'dbdatagateway', kind: 'event', subject: noticeTitle[notice.kind] ?? notice.kind, state: 'event', detail: notice.message, held: false, target: null }
}

/** What a Gateway says in `GET /api/v1/certificate`, and what a Sync server says in `GET /api/admin/certificate` (its `certificate` and `expiryWarningDays`): the figures the certificate screens show, read here so that an expiry is seen before a login fails. */
export interface GatewayCertificate { present: boolean; subject?: string; days_left?: number; needs_renewal?: boolean; renew_before_days: number; warning?: string }
export interface SyncCertificate { available: boolean; certificate: { subjectCommonName: string; daysRemaining: number } | null; expiryWarningDays: number }

function certificateLine(subject: string, days: number): { state: 'failed' | 'expiring'; detail: string } {
  if (days < 0) return { state: 'failed', detail: `The certificate for ${subject} expired ${-days} day${days === -1 ? '' : 's'} ago` }
  return { state: 'expiring', detail: `The certificate for ${subject} ends in ${days} day${days === 1 ? '' : 's'}` }
}

const products = ['dbdatasync', 'dbdatabuild', 'dbdatagateway', 'dbdatarunner', 'dbdatastore', 'dbdataworkspace', 'dbdatakit'] as const
const productOf = (product: string | null): MonitorRow['product'] => products.find((k) => k === product) ?? 'unknown'

/** The console's own certificate as a server of its own for the rows: `GET /api/v1/certificate` of the console, not through the relay (UI10). */
export const consoleServer: InstanceSummary = { id: 'console', name: 'This console', url: '', product: 'dbdatakit', version: null, apiVersion: null, reachable: true, ready: true, problem: null, pairedAt: null }

/** A certificate of a server that is expired (a failure) or within its warning window (expiring); null when it is fine, when the server has none, or when it cannot say. A valid certificate is no row. It has no buttons: renewing is on the server's own certificate screen. */
export function certificateRow(server: InstanceSummary, cert: GatewayCertificate | SyncCertificate): MonitorRow | null {
  let subject: string, days: number, window: number
  if ('present' in cert) {
    if (!cert.present || cert.days_left === undefined) return null
    subject = (cert.subject ?? server.name).replace(/^CN=/i, ''); days = cert.days_left; window = cert.renew_before_days
    if (cert.needs_renewal !== true && days > window) return null
  } else {
    if (!cert.available || cert.certificate === null) return null
    subject = cert.certificate.subjectCommonName; days = cert.certificate.daysRemaining; window = cert.expiryWarningDays
    if (days > window) return null
  }
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
  const known = ['dbdatasync', 'dbdatabuild', 'dbdatagateway', 'dbdatarunner', 'dbdatastore', 'dbdataworkspace'] as const
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
      if (server.product === 'dbdatasync') {
        // the certificate is asked on its own: a server that cannot say (no certificate screen, a refusal) shows no row for it and the replications still show
        const cert = await request<SyncCertificate>(relay(server, '/api/admin/certificate')).then((c) => certificateRow(server, c), () => null)
        if (cert) rows.push(cert)
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
        await certificateOf(server, rows)
        const { projects } = await request<{ projects: { id: string }[] }>(relay(server, '/api/v1/projects'))
        for (const project of projects) {
          const { jobs } = await request<{ jobs: BuildJob[] }>(relay(server, `/api/v1/projects/${enc(project.id)}/schedule`))
          rows.push(...jobs.map((job) => buildRow(server, project.id, job)))
        }
      } else if (server.product === 'dbdatagateway') {
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
      } else if (server.product === 'dbdatastore' || server.product === 'dbdataworkspace') {
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

/** What moving a Build plan to another scheduler sends (`PUT .../schedule/{connection}/{plan}/owner`): the move is the Build server's, which changes its own file and the runner's record in the order that never schedules the plan twice. */
export interface PlanMove { owner: 'old' | 'observe' | 'runner'; reason: string; runner?: string; channel?: string }

/** Why a plan's move cannot be sent yet, in words; null when it can. The server decides again (the operator role, whether the runner serves the package). */
export function moveBlock(row: MonitorRow, move: PlanMove): string | null {
  if (row.kind !== 'plan') return 'Only a Build plan moves from here.'
  if (move.owner === (row.owner ?? 'old')) return `The plan is already ${row.owner === 'runner' ? "the runner's" : row.owner === 'observe' ? 'observed' : "this server's"}.`
  if (move.reason.trim() === '') return 'Say why: the reason is recorded with who moved it.'
  if (move.owner !== 'old' && row.runner === undefined && ((move.runner ?? '').trim() === '' || (move.channel ?? '').trim() === '')) return 'Name the runner and its channel: this server has none written down yet.'
  if (move.owner === 'observe' && row.owner === 'runner') return 'The runner schedules it: roll it back first, then observe.'
  return null
}

/** Moves a Build plan (a row of kind `plan`) to another scheduler. */
export function movePlan(row: MonitorRow, move: PlanMove) {
  if (row.target === null || !('project' in row.target)) return Promise.resolve(null)
  const { project, connection, plan } = row.target
  const body: Record<string, string> = { owner: move.owner, reason: move.reason.trim() }
  if (move.owner !== 'old' && row.runner === undefined) { body.runner = (move.runner ?? '').trim(); body.channel = (move.channel ?? '').trim() }
  return request<{ owner: string; runner: string; channel: string; waited: boolean }>(`/relay/${row.server}/api/v1/projects/${enc(project)}/schedule/${enc(connection)}/${enc(plan)}/owner`, { method: 'PUT', body: JSON.stringify(body) })
}
