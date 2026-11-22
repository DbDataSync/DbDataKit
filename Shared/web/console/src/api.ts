import { request } from '@dbdatakit/shell'
import type { LineageGraphDoc, LineageRunDoc } from '@dbdatakit/visuals'

/** A server this console manages, with how it answered when it was last asked. */
export interface InstanceSummary {
  id: string
  name: string
  url: string
  product: string | null
  version: string | null
  apiVersion: number | null
  reachable: boolean
  ready: boolean | null
  problem: string | null
  pairedAt: string | null
  /** The compatibility generation the server reported, or the one last recorded for it (plan 24a UC05); null when it states none. */
  generation?: number | null
  /** Set when the server is further from this console than N and N-1: how far apart they are and which side to upgrade. */
  outsideSupport?: string | null
  /** On a pairing of a runner (plan 28c E20d): null when the instance was given its group's key, else why not (the pairing stands; join it again below). */
  groupJoin?: string | null
}

/** A server that announced itself and waits for an administrator to confirm it (plan 13a, L03). The code it announced is never sent here. */
export interface PendingServer {
  id: string
  name: string
  product: string
  url: string
  /** When the server last announced itself; it announces again every half minute while it waits. */
  lastSeen: string
}

/** What one server said when the console asked for its lineage (plan 18, G09): how big its graph is, what it left out, or why it gave none. */
export interface LineageServer {
  server: string
  product: string | null
  nodes: number | null
  edges: number | null
  problems: string[]
  error: string | null
  at: string
}

/** What a block stops (plan 23a FQ16): the node whose check blocks, the checks, since when, and the nodes downstream of it. */
export interface LineageBlock {
  node: string
  name: string
  checks: string[]
  since: string | null
  stops: { id: string; name: string; kind: string; depth: number }[]
}

/** The joined graph of the servers and what each said. `error` is set when their graphs cannot be joined. Nodes whose datasets have checks carry `properties.check_status` (`blocked`, `fail`, `error` or `pass`), and `blocks` lists what each block stops. */
export interface LineageAnswer extends LineageGraphDoc {
  fingerprint: string
  servers: LineageServer[]
  blocks?: LineageBlock[]
  /** The newest run of each edge the servers know (plan 18 G10b): a failed one is marked on its line. */
  runs?: LineageRunDoc[]
  error?: string
}

/** One runner's place in a channel's deployment of a definition (plan 28c E24). */
export interface DeployedInstance {
  instance: string
  host?: string
  version: number
  state: string
  reported_at: string
  error?: string
  runtime?: string
  live: boolean
  on_active: boolean
}

/** A definition in one channel: its versions, the latest change with who and when, and the runners. */
export interface DeployedChannel {
  group: string
  kind: string
  name: string
  channel: string
  active?: number
  previous?: number
  staged?: number
  last_event?: string
  last_by?: string
  last_at?: string
  history?: string
  instances: DeployedInstance[]
}

/** A definition in a channel as one runner holds it. */
export interface RunnerHolding {
  kind: string
  name: string
  channel: string
  version: number
  state: string
  reported_at: string
  error?: string
  channel_active?: number
  behind: boolean
}

export interface DeployedRunner {
  instance: string
  group: string
  host?: string
  runtime?: string
  runner_version?: string
  live: boolean
  holdings: RunnerHolding[]
}

export interface DeploymentIndex {
  definitions: { kind: string; name: string }[]
  runners: { instance: string; host?: string; live: boolean }[]
  servers: { server: string; error: string | null; nodes: number | null }[]
}

/** What the Add a server screen sends to make a join token: the server, the role ceiling, the group, how long it works and the console's address as that server reaches it. */
export interface JoinTokenRequest {
  product: string
  roles: string[]
  group: string | null
  minutes: number | null
  url: string | null
}

/** A join token just made: the command that spends it, shown once, and the token's id, scope and end. */
export interface JoinTokenMade {
  command: string
  tokenId: string
  product: string
  roles: string[]
  group: string | null
  expires: string
  /** The command names a loopback address, which only a server on the console's machine reaches. */
  loopback: boolean
}

/** A join token still waiting to be used: never its value. */
export interface WaitingJoinToken {
  id: string
  product: string
  roles: string[]
  group: string | null
  madeBy: string
  madeAt: string
  expires: string
  tries: number
}

export const instancesKey = ['instances'] as const
export const joinTokensKey = ['join-tokens'] as const
export const pendingKey = ['instances', 'pending'] as const

export const consoleApi = {
  /** The lineage of every server that keeps one, joined by identity; `refresh` asks the servers again instead of using what was kept for half a minute. */
  lineage: (refresh = false) => request<LineageAnswer>(`/api/lineage${refresh ? '?refresh=true' : ''}`),
  deployments: {
    /** The definitions and runners the paired runners know. */
    index: (refresh = false) => request<DeploymentIndex>(`/api/lineage/deployments${refresh ? '?refresh=true' : ''}`),
    /** Where is this definition deployed? */
    where: (definition: string) => request<{ definition: string; channels: DeployedChannel[] }>(`/api/lineage/deployments?definition=${encodeURIComponent(definition)}`),
    /** What is deployed on this runner (an instance id or a host)? */
    on: (runner: string) => request<{ runner: string; instances: DeployedRunner[] }>(`/api/lineage/deployments?runner=${encodeURIComponent(runner)}`),
  },
  instances: {
    list: () => request<InstanceSummary[]>('/api/instances'),
    pair: (url: string, code: string, name: string | null) =>
      request<InstanceSummary>('/api/instances', { method: 'POST', body: JSON.stringify({ url, code, name }) }),
    pending: () => request<PendingServer[]>('/api/instances/pending'),
    /** Pairs with a waiting server, as the administrator's confirmation. */
    confirm: (id: string) => request<InstanceSummary>(`/api/instances/pending/${encodeURIComponent(id)}/confirm`, { method: 'POST' }),
    /** Forgets a waiting server without pairing; it is listed again if it keeps announcing. */
    reject: (id: string) => request<void>(`/api/instances/pending/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    forget: (id: string) => request<void>(`/api/instances/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    /** Joins another instance of a runner group (its address and a pairing code made on it), or the registered one again (no address): the console hands it the group's key (plan 28c E20d). */
    join: (id: string, url: string | null, code: string | null) =>
      request<{ server: string; instance: string; already: boolean }>(`/api/instances/${encodeURIComponent(id)}/join`, { method: 'POST', body: JSON.stringify({ url, code }) }),
    /** Pairs a Runner with a gateway so it can tell the gateway's cache when it wrote data; no code is typed (T11, 24a UC13). */
    link: (from: string, to: string, name: string | null) =>
      request<{ from: string; to: string; name: string }>('/api/instances/link', { method: 'POST', body: JSON.stringify({ from, to, name }) }),
  },
  /** The single-use tokens a server joins the console with (the Add a server screen). */
  joinTokens: {
    waiting: () => request<WaitingJoinToken[]>('/api/join-tokens'),
    make: (body: JoinTokenRequest) => request<JoinTokenMade>('/api/join-tokens', { method: 'POST', body: JSON.stringify(body) }),
    cancel: (id: string) => request<void>(`/api/join-tokens/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  },
}
