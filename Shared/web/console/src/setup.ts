import { request, type UserSummary } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'
import { loadConnections } from './connections'

/** One step of the setup checklist (plan 23a FQ21). The ids are the server's (`SetupItems.Known`); the words and the page an item opens are the screen's. */
export interface SetupItemDef {
  id: string
  title: string
  why: string
  /** The page that does it. */
  to: string
  /** Null when the step can be done; otherwise why it cannot yet (a slice that is not built). */
  unavailable: string | null
}

export const SETUP_ITEMS: SetupItemDef[] = [
  { id: 'sign-in', title: 'Choose how people sign in', why: 'Passkeys are on from the start. Connect an identity home for Windows, OpenID Connect or Entra ID sign-in, and invite the people who need access.', to: '/users', unavailable: null },
  { id: 'servers', title: 'Pair your first server', why: 'The console manages servers (Build, Sync, the Gateway, the Store, a runner). A server on this machine can be confirmed from its announcement; one on another machine is paired with its address and code.', to: '/instances', unavailable: null },
  { id: 'connections', title: 'Add your first connection', why: 'A connection says where the data is: a Sync source or target, or a data source the Gateway serves.', to: '/connections', unavailable: null },
  { id: 'git-remotes', title: 'Connect a git remote for the configuration', why: 'Definitions are kept in a git repository so every change has an author and a history.', to: '/instances', unavailable: 'Not built yet (plan 22a GP19).' },
  { id: 'delivery-channels', title: 'Set up where alerts are sent', why: 'A delivery channel (mail, Slack, Teams or a signed webhook) is a connection the console sends through; alerts, reports and subscriptions use it.', to: '/delivery', unavailable: null },
  { id: 'quality-alerts', title: 'Send failed checks to a channel', why: 'Name a delivery channel and the people who get a message when a data quality check fails and when it passes again (the section quality: alerts: of settings.yaml).', to: '/quality', unavailable: null },
  { id: 'build-project', title: 'Make a first Build project from a template', why: 'A template gives a project with models, a connection and a plan to run.', to: '/instances', unavailable: null },
]

/** What the install has, read from the pages' own data: a step is finished by this and by nothing a person clicks. */
export interface SetupFacts {
  /** A person with a passkey (or another way in) is enabled. */
  signIn: boolean
  servers: boolean
  connections: boolean
  /** At least one delivery channel is configured (the Delivery page's overview lists one). */
  deliveryChannels: boolean
  /** The alerts of failed checks name a channel the install has. */
  qualityAlerts: boolean
  /** Build servers that answered, and how many projects they host: null when no Build server answered. */
  buildProjects: number | null
}

export type SetupStatus = 'done' | 'todo' | 'skipped' | 'unavailable'

export interface SetupRow extends SetupItemDef {
  status: SetupStatus
}

/**
 * The checklist for the facts and the skipped ids. **Done wins over skipped** (a thing the install has is done whatever was clicked) and an unavailable step is shown as such and never nags. A skipped step leaves the banner and stays
 * in this list, where it can be put back.
 */
export function checklist(facts: SetupFacts, skipped: ReadonlySet<string>): SetupRow[] {
  const done: Record<string, boolean> = {
    'sign-in': facts.signIn, servers: facts.servers, connections: facts.connections, 'delivery-channels': facts.deliveryChannels, 'quality-alerts': facts.qualityAlerts, 'build-project': (facts.buildProjects ?? 0) > 0,
  }
  return SETUP_ITEMS.map((item) => ({
    ...item,
    status: done[item.id] ? 'done' : item.unavailable ? 'unavailable' : skipped.has(item.id) ? 'skipped' : 'todo',
  }))
}

/** The steps the banner counts: still to do and not skipped. */
export const remaining = (rows: readonly SetupRow[]) => rows.filter((r) => r.status === 'todo')

export interface SkippedItem { item: string; skipped: boolean; skipped_at: string | null }

export const setupApi = {
  items: () => request<{ items: SkippedItem[] }>('/api/setup/items'),
  skip: (item: string, skipped: boolean) => request<void>(`/api/setup/items/${encodeURIComponent(item)}`, { method: 'PUT', body: JSON.stringify({ skipped }) }),
}

/** Reads the facts: the users, the paired servers, the connections and the Build servers' projects. A page that does not answer counts as nothing there (the step stays to do). */
export async function loadFacts(servers: InstanceSummary[], users: UserSummary[]): Promise<SetupFacts> {
  const builds = servers.filter((s) => s.reachable && s.product === 'dbdatabuild')
  const projects = await Promise.all(builds.map(async (s) => {
    try { return (await request<{ projects: unknown[] }>(`/relay/${encodeURIComponent(s.id)}/api/v1/projects`)).projects.length } catch { return null }
  }))
  const answered = projects.filter((p): p is number => p !== null)
  let connections = false
  try { connections = (await loadConnections(servers)).rows.length > 0 } catch { connections = false }
  let deliveryChannels = false
  try { deliveryChannels = (await request<{ channels: unknown[] }>('/api/delivery')).channels.length > 0 } catch { deliveryChannels = false }
  let qualityAlerts = false
  try { const a = await request<{ configured: boolean; channelKnown: boolean }>('/api/quality/alerts'); qualityAlerts = a.configured && a.channelKnown } catch { qualityAlerts = false }
  return {
    signIn: users.some((u) => u.enabled && u.credentials.length > 0),
    servers: servers.length > 0,
    connections,
    deliveryChannels,
    qualityAlerts,
    buildProjects: answered.length === 0 ? null : answered.reduce((a, b) => a + b, 0),
  }
}
