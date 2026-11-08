import { request } from '@dbdatakit/shell'
import type { LineageGraphDoc } from '@dbdatakit/visuals'

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

/** The joined graph of the servers and what each said. `error` is set when their graphs cannot be joined. */
export interface LineageAnswer extends LineageGraphDoc {
  fingerprint: string
  servers: LineageServer[]
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

export const instancesKey = ['instances'] as const
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
    /** Pairs a Build server with a proxy so Build can publish to it; no code is typed (T11). */
    link: (from: string, to: string, name: string | null) =>
      request<{ from: string; to: string; name: string }>('/api/instances/link', { method: 'POST', body: JSON.stringify({ from, to, name }) }),
  },
}
