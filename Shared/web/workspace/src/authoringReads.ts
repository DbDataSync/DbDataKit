import { request } from '@dbdatakit/shell'
import type { StagedRun } from './api'
import type { DeployRecord, RunnerVersion } from './deploys'
import type { ChannelList } from './catalogRefresh'

// The Designer's other reads of a draft's connection, asked of the console as the signed-in person, as Refresh is: the Runner's test of the slot's binding in
// a channel, the columns of a query (the Runner's built-in `describe`), and the newest run of a check (a package's `preflight`) per channel of what the
// draft published. The Workspace server is asked only whether the person may read the project; it calls no Runner and holds no login.
// Design: docs/authoring-metadata.md#every-kind-uses-the-same-pieces

export type TestStepName = 'binding' | 'connection' | 'login' | 'connect'

/** One step of the Runner's binding test: `passed`, `failed`, or `skipped` (not tried, because an earlier step failed). */
export interface TestStep { step: TestStepName; state: 'passed' | 'failed' | 'skipped' }

/** What a test of a draft's connection is asked: the Runner (its id in the console), the channel, the project's connection and the role of its login. */
export interface DraftTestRequest { runner: string; channel: string; connection: string; role?: 'default' | 'read' | 'write' }

/** What the console answers for a test: whether the binding passed, the step the Runner stopped at, why in words, and each step. Never a row, an address or a login. */
export interface DraftTestAnswer {
  package: string; connection: string; slot: string; channel: string; production: boolean; role: string; runner: string; runner_name: string
  ok: boolean; step: TestStepName | null; message: string | null; connected: boolean; bound_to: string | null; steps: TestStep[]
}

/** What a description of a query is asked: the Runner, the channel, the project's connection the query reads, and the query. */
export interface DescribeRequest { runner: string; channel: string; connection: string; sql: string }

/** One column of a described query: its name, its type as the engine names it, and whether it may be null. */
export interface DescribedColumn { name: string; type: string; nullable: boolean }

/** Where a description stands: still going (with the run's link), done with the query's columns, or failed with the Runner's reason. */
export interface DescribeAnswer {
  state: 'done' | 'running' | 'failed'; run_id: string; link: string; runner: string; runner_name: string; channel: string; production: boolean; connection: string
  engine?: string; strategy?: string; columns?: DescribedColumn[]; error?: string
}

/** The newest run of a task in a deploy's channel (`GET /api/deploys/{id}/runs?task=&step=`), with the steps it recorded; `run` is null when the task never ran there. */
export interface NewestRun { package: string; channel: string; task: string; runner: string; runner_name: string; link: string | null; run: StagedRun | null }

const enc = encodeURIComponent
const base = (workspace: string, project: string) => `/api/workspace-catalog/${enc(workspace)}/${enc(project)}`

/** The console's routes of these reads. They are the console's own, whatever server the module talks to. */
export const authoringReadApi = {
  test: (workspace: string, project: string, body: DraftTestRequest) =>
    request<DraftTestAnswer>(`${base(workspace, project)}/binding-test`, { method: 'POST', body: JSON.stringify(body) }),
  describe: (workspace: string, project: string, body: DescribeRequest) =>
    request<DescribeAnswer>(`${base(workspace, project)}/describe`, { method: 'POST', body: JSON.stringify(body) }),
  followDescribe: (workspace: string, project: string, run: string, body: Omit<DescribeRequest, 'sql'>) =>
    request<DescribeAnswer>(`${base(workspace, project)}/describe/${enc(run)}?runner=${enc(body.runner)}&channel=${enc(body.channel)}&connection=${enc(body.connection)}`),
  newest: (deploy: string, task: string, step?: string) =>
    request<NewestRun>(`/api/deploys/${enc(deploy)}/runs?task=${enc(task)}${step ? `&step=${enc(step)}` : ''}`),
}

/** What each step of the binding test tries, in words. */
export const TEST_STEP_WORDS: Record<TestStepName, string> = {
  binding: 'the slot is bound in the channel',
  connection: 'the Runner has the connection the binding names',
  login: 'the login\'s secrets resolve on the Runner',
  connect: 'the connection opens',
}

/** The line a test ends with: passed through which connection, or the Runner's reason. */
export function testLine(answer: DraftTestAnswer): string {
  if (answer.ok) return answer.message ?? `${answer.connection} opened in ${answer.channel}${answer.bound_to ? ` through ${answer.bound_to}` : ''}.`
  return answer.message ?? `The test of ${answer.connection} in ${answer.channel} stopped at ${answer.step ?? 'its first step'}.`
}

/** The columns of a described query in one line: `3 columns: id int, name nvarchar(50), segment nvarchar(20) null`. */
export function describedLine(columns: readonly DescribedColumn[]): string {
  if (columns.length === 0) return 'The query has no column.'
  return `${columns.length} ${columns.length === 1 ? 'column' : 'columns'}: ${columns.map((c) => `${c.name} ${c.type}${c.nullable ? ' null' : ''}`).join(', ')}`
}

/**
 * Whether the picker offers **Set up for development**: a channel other than production the Runner lists does not bind the slot, or no channel binds it at
 * all (also when the Runner holds no version of the package yet, and lists no channel). Production is never set up from a screen.
 */
export function setUpOffered(list: ChannelList | undefined): boolean {
  if (!list) return false
  return list.channels.some((c) => !c.production && !c.bound) || !list.channels.some((c) => c.bound)
}

/** The channels a development login can be set up for: those the Runner lists that are not production and do not bind the slot; `dev` when it lists none but production. */
export function setUpChannels(list: ChannelList | undefined): string[] {
  const others = (list?.channels ?? []).filter((c) => !c.production)
  return others.length === 0 ? ['dev'] : others.filter((c) => !c.bound).map((c) => c.channel)
}

/** What is left once the login is stored and the channel does not bind the slot yet: the binding, by an administrator on the Runner. */
export const bindingLeftLine = (slot: string, channel: string, runner: string, secret: string) =>
  `The slot ${slot} is not bound in ${channel} yet. An administrator binds it on the Packages page of ${runner} (Bindings), to a connection whose password names the secret ${secret}; the picker offers ${channel} once it binds the slot.`

/** The newest deploy to a Runner of each channel (by Runner and channel), from the console's records, newest first: what a newest-check card reads per channel. */
export function newestPerChannel(deploys: readonly DeployRecord[], pkg?: string): RunnerVersion[] {
  const seen = new Set<string>()
  const out: RunnerVersion[] = []
  for (const d of deploys) {
    if (d.outcome !== 'ok' || d.product !== 'runner' || !d.name || !d.channel || d.version == null || (pkg && d.name !== pkg)) continue
    const key = `${d.target}\u0000${d.channel}`
    if (seen.has(key)) continue
    seen.add(key)
    out.push({ deploy: d.id, build: d.build, target: d.target, targetName: d.target_name, package: d.name, channel: d.channel, version: String(d.version), mode: d.mode ?? 'stage', tasks: d.tasks ?? [] })
  }
  return out
}
