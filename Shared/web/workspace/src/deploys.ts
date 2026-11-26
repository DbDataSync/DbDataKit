// The console's deploy of a build (Design: docs/publishing.md#deploying-a-build-from-the-control-service): the Workspace's screens ask the console that hosts them,
// never the Workspace server, to send a build to a Runner or a Gateway, and to run what a deploy put on a Runner (Design: docs/publishing.md#runs-of-a-deployed-build).
// These calls go to the host whatever the module's API base (`apiUrl` keeps `/api/deploys` and `/api/instances` on the host).
import { useQuery } from '@tanstack/react-query'
import { request, useModule } from '@dbdatakit/shell'
import type { BindingTestAnswer, ProjectKindInfo, PublishOptions, PublishedAnswer, StagedRun, StagedRunStarted } from './api'
import type { DeployPreflight } from './pages/PreflightCard'

/** A server of the console as `GET /api/instances` lists it (only what the publish panel needs). */
export interface ConsoleServer { id: string; name: string; product: string | null; reachable: boolean }

/** A Runner or a Gateway the console manages, as the publish panel offers it: `accepts` is whether the project's kind goes to its product. */
export interface DeployTarget { id: string; name: string; product: 'runner' | 'gateway'; reachable: boolean; accepts: boolean }

/** What `POST /api/deploys` takes: the build, the target (the console's id or name of the server) and what the deploy of the kind takes. */
export interface DeployBody {
  workspaceServer: string; project: string; workspace: string; build: string; target: string
  channel?: string; serve?: boolean; expectedVersion?: number; bind?: Record<string, string>; dataSource?: string; adopt?: string[]; model?: string; note?: string
  /** An app build sent as the preview of this instance of the Gateway, in its `preview` channel, in place of a version. */
  previewInstance?: string
}

/** The preview a deploy of an app made: the instance, and the preview's address and path on the Gateway's apps host (the address is null when the Gateway runs no apps host). */
export interface DeployPreview { instance: string | null; address: string | null; path: string | null }

/** One deploy the console keeps (`GET /api/deploys`): what was sent where, by whom, and what the target answered; a refused deploy has its `error`. */
export interface DeployRecord {
  id: string; workspace_server: string; project: string; workspace: string; build: string; item: string; kind: string
  target: string; target_name: string; product: string; route: string; name: string | null; channel: string | null; version: number | null; mode: string | null
  signature: string | null; deployed_by: string; deployed_at: string; outcome: string; error: string | null
  /** The tasks the Runner said the package has (none for what is not a Runner package). */
  tasks?: string[]
  /** The preflight the deploy waited for before it served the version in production, and what became of the activation; null when it waited for none. */
  preflight?: DeployPreflight | null
  /** The preview of an instance the deploy sent the app build to; null for a deploy that is not a preview. */
  preview?: DeployPreview | null
}

/** A run started through the console on what a deploy put on a Runner (`POST /api/deploys/{id}/runs`). */
export interface DeployRunAnswer { steps: string[]; run: StagedRunStarted }

/** What a person approves of a Store model's contract: the run that read the plan, its hash, and the consumers allowed by name. */
export interface ContractBody { planRun: string; approve: string; allowConsumer: string[] }

/** What a deploy answered: the steps, the target's own answer and the record the console kept. */
export interface DeployAnswer { steps: string[]; deployed: PublishedAnswer & { preflight?: DeployPreflight }; deploy: DeployRecord }

/** What `POST /api/deploys/{id}/activate` answered: what became of the activation, in words, the version active now and the preflight it was checked against. */
export interface ActivateAnswer { activation: string; message: string; active: number | null; preflight: DeployPreflight; deploy: DeployRecord }

/** Which deploys to list: the Workspace server's, narrowed by project, workspace and build. */
export interface DeployQuery { workspaceServer: string; project?: string; workspace?: string; build?: string }

export const deployApi = {
  servers: () => request<ConsoleServer[]>('/api/instances'),
  deploy: (body: DeployBody) => request<DeployAnswer>('/api/deploys', { method: 'POST', body: JSON.stringify(body) }),
  deploys: (query: DeployQuery) => {
    const params = new URLSearchParams({ workspace_server: query.workspaceServer })
    if (query.project) params.set('project', query.project)
    if (query.workspace) params.set('workspace', query.workspace)
    if (query.build) params.set('build', query.build)
    return request<{ deploys: DeployRecord[] }>(`/api/deploys?${params}`).then((r) => r.deploys)
  },
  /** Starts a task of what the deploy put on a Runner, for the signed-in person: of the version it staged (a manual-only task), or with `served` of the version its channel serves now. */
  run: (deploy: string, task: string, inputs: Record<string, string>, served = false) =>
    request<DeployRunAnswer>(`/api/deploys/${encodeURIComponent(deploy)}/runs`, { method: 'POST', body: JSON.stringify(served ? { task, inputs, served } : { task, inputs }) }),
  /** A run on the deploy's Runner, with the steps named `step` it recorded; 404 while it is still queued. */
  runOf: (deploy: string, run: string, step?: string) =>
    request<{ run: StagedRun }>(`/api/deploys/${encodeURIComponent(deploy)}/runs/${encodeURIComponent(run)}${step ? `?step=${encodeURIComponent(step)}` : ''}`).then((r) => r.run),
  /** The Runner's test of the login the deploy's channel binds to a slot. */
  bindingTest: (deploy: string, slot: string, role: 'default' | 'read' | 'write' = 'default') =>
    request<{ test: BindingTestAnswer }>(`/api/deploys/${encodeURIComponent(deploy)}/binding-test`, { method: 'POST', body: JSON.stringify({ slot, role }) }).then((r) => r.test),
  /** Activates the version a deploy staged in production for its preflight, once the run has ended; `confirm` when the person confirms the errors it found (kept in the Runner's record). */
  activate: (deploy: string, confirm: boolean) =>
    request<ActivateAnswer>(`/api/deploys/${encodeURIComponent(deploy)}/activate`, { method: 'POST', body: JSON.stringify({ confirm }) }),
  /** Runs a Store model's approved contract plan, after the console asked the Gateways it manages who reads what it drops (409 naming them, unless each is allowed). */
  contract: (deploy: string, body: ContractBody) =>
    request<DeployRunAnswer & { checked: number; gateways: number }>(`/api/deploys/${encodeURIComponent(deploy)}/contract`, { method: 'POST', body: JSON.stringify(body) }),
}

/** What a deploy put on a channel of a Runner, as the Runs, Provisioning and Deploy tabs run it: the deploy's id, the build, the Runner (the console's id and its name), the package, channel, version, mode and tasks. */
export interface RunnerVersion { deploy: string; build: string; target: string; targetName: string; package: string; channel: string; version: string; mode: string; tasks: string[] }

/** The newest deploy that put a version on a Runner (of `pkg` when one is named), from the console's records, newest first; null when none did. A refused deploy and one to a Gateway are not one. */
export function runnerVersionOf(deploys: readonly DeployRecord[], pkg?: string): RunnerVersion | null {
  const d = deploys.find((r) => r.outcome === 'ok' && r.product === 'runner' && r.name && r.channel && r.version != null && (!pkg || r.name === pkg))
  return d ? { deploy: d.id, build: d.build, target: d.target, targetName: d.target_name, package: d.name!, channel: d.channel!, version: String(d.version), mode: d.mode ?? 'stage', tasks: d.tasks ?? [] } : null
}

/** The Workspace server these screens show, as the console knows it (its id); null when they are not hosted by a console. */
export function useHostedServer(): string | null {
  const module = useModule()
  return module.host?.serverId ?? hostedServer(module.apiBase)
}

/** The version a deploy of a build of this draft put on a Runner (of `pkg` when one is named), from the console's record of the draft's deploys. */
export function useRunnerVersion(project: string, id: string, pkg?: string) {
  const server = useHostedServer()
  return useQuery({
    queryKey: ['console', 'deploys', server, project, id, 'runner-version', pkg ?? null],
    queryFn: async () => runnerVersionOf(await deployApi.deploys({ workspaceServer: server!, project, workspace: id }), pkg),
    enabled: server !== null,
  })
}

/** The id of the server the console relays to, from the module's API base (`/relay/<id>`); null when the screens are not hosted by a console. */
export function hostedServer(apiBase: string): string | null {
  const m = /^\/relay\/([^/]+)\/?$/.exec(apiBase)
  return m ? decodeURIComponent(m[1]!) : null
}

const productOf = (server: string | null): 'runner' | 'gateway' | null => (server === 'dbdatarunner' ? 'runner' : server === 'dbdatagateway' ? 'gateway' : null)

/**
 * The Runners and Gateways a build can be deployed to: every server the console manages that is one, by name; `accepts` says whether the kind's routes
 * (`GET /api/v1/project-kinds`) go to its product. An unknown kind accepts none.
 */
export function deployTargets(servers: readonly ConsoleServer[], kind: ProjectKindInfo | undefined): DeployTarget[] {
  const products = new Set((kind?.publishes ?? []).map((p) => p.product))
  return servers
    .flatMap((s) => { const product = productOf(s.product); return product ? [{ id: s.id, name: s.name, product, reachable: s.reachable, accepts: products.has(product) }] : [] })
    .sort((a, b) => a.name.localeCompare(b.name))
}

/** The body of a deploy from what the publish form gathered: the build's place, the target and the form's options in the console's names. */
export function deployBody(build: { workspaceServer: string; project: string; workspace: string; build: string }, target: string, note: string, options: PublishOptions): DeployBody {
  const body: DeployBody = { ...build, target }
  if (note) body.note = note
  if (options.channel) body.channel = options.channel
  if (options.serve) body.serve = true
  if (options.bind && Object.keys(options.bind).length > 0) body.bind = options.bind
  if (options.data_source) body.dataSource = options.data_source
  if (options.adopt && options.adopt.length > 0) body.adopt = options.adopt
  if (options.model) body.model = options.model
  return body
}

/** A kept deploy as one line: what the target holds now (a package's version on a channel, the models, an app's version), or the refusal in the target's words. */
export function deployLine(d: DeployRecord): string {
  if (d.outcome !== 'ok') return d.error ?? 'Refused.'
  const what = d.name ?? d.item
  const version = d.version != null ? ` as version ${d.version}` : ''
  if (d.mode === 'preview') return `Sent ${what} to ${d.target_name} as the preview of ${d.preview?.instance ?? 'an instance'}${d.version != null ? ` (version ${d.version} of the preview channel)` : ''}.`
  if (d.product === 'runner') return `${d.mode === 'serve' ? 'Published' : 'Staged'} ${what} on ${d.channel ?? 'its channel'} of ${d.target_name}${version}.`
  return `Published ${what} to ${d.target_name}${version}.`
}
