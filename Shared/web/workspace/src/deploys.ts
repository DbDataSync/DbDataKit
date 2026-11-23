// The console's deploy of a build (Design: docs/publishing.md#deploying-a-build-from-the-control-service): the Workspace's screens ask the console that hosts them,
// never the Workspace server, to send a build to a Runner or a Gateway. These calls go to the host whatever the module's API base (`apiUrl` keeps `/api/deploys`
// and `/api/instances` on the host).
import { request } from '@dbdatakit/shell'
import type { ProjectKindInfo, PublishOptions, PublishedAnswer } from './api'

/** A server of the console as `GET /api/instances` lists it (only what the publish panel needs). */
export interface ConsoleServer { id: string; name: string; product: string | null; reachable: boolean }

/** A Runner or a Gateway the console manages, as the publish panel offers it: `accepts` is whether the project's kind goes to its product. */
export interface DeployTarget { id: string; name: string; product: 'runner' | 'gateway'; reachable: boolean; accepts: boolean }

/** What `POST /api/deploys` takes: the build, the target (the console's id or name of the server) and what the deploy of the kind takes. */
export interface DeployBody {
  workspaceServer: string; project: string; workspace: string; build: string; target: string
  channel?: string; serve?: boolean; expectedVersion?: number; bind?: Record<string, string>; dataSource?: string; adopt?: string[]; model?: string; note?: string
}

/** One deploy the console keeps (`GET /api/deploys`): what was sent where, by whom, and what the target answered; a refused deploy has its `error`. */
export interface DeployRecord {
  id: string; workspace_server: string; project: string; workspace: string; build: string; item: string; kind: string
  target: string; target_name: string; product: string; route: string; name: string | null; channel: string | null; version: number | null; mode: string | null
  signature: string | null; deployed_by: string; deployed_at: string; outcome: string; error: string | null
}

/** What a deploy answered: the steps, the target's own answer and the record the console kept. */
export interface DeployAnswer { steps: string[]; deployed: PublishedAnswer; deploy: DeployRecord }

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
  if (d.product === 'runner') return `${d.mode === 'serve' ? 'Published' : 'Staged'} ${what} on ${d.channel ?? 'its channel'} of ${d.target_name}${version}.`
  return `Published ${what} to ${d.target_name}${version}.`
}
