import { request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'

/** One thing that waits for a person's decision, on one server: a change request of a Store model, or an app version of a Gateway that asks to be trusted and has no approval for its exact files. */
export interface ApprovalRow {
  /** Unique among the rows: the server and what waits. */
  key: string
  server: string
  serverName: string
  product: 'dbdatastore' | 'dbdatagateway'
  kind: 'change-request' | 'app-trust'
  /** What waits, as the owning screen names it: the model (and instance), or `<app> version <n>`. */
  subject: string
  detail: string
  /** Who proposed or published it; null when the server did not say. */
  by: string | null
  /** An agent's write: shown as such, since it is the case the approval exists for. */
  byAgent: boolean
  /** When it was proposed or published (ISO), null when unknown. */
  since: string | null
  /** Whether the signed-in person may decide it, as the server says; the screen that decides asks again. */
  canDecide: boolean
  /** The console route of the screen where it is read and decided. */
  link: string
}

/** The Store's list of a model's requests (`GET /api/v1/models/{m}/requests?state=pending`). */
export interface StoreRequestList {
  requests: { id: string; model: string; instance: string; state: string; proposed_by: string; proposed_by_name: string | null; by_agent: boolean; proposed_at: string; description: string | null; change_count: number }[]
  can_decide: boolean
  decision_needs: string
}

/** The pending requests of one model copy as rows. */
export function storeRows(server: InstanceSummary, model: string, list: StoreRequestList): ApprovalRow[] {
  return list.requests.filter((r) => r.state === 'pending').map((r) => {
    const instance = r.instance && r.instance !== 'default' ? ` (instance ${r.instance})` : ''
    const changes = `${r.change_count} change${r.change_count === 1 ? '' : 's'}`
    return {
      key: `${server.id}/${model}/${r.instance}/${r.id}`, server: server.id, serverName: server.name, product: 'dbdatastore', kind: 'change-request',
      subject: `${model}${instance}`, detail: r.description ? `${r.description} (${changes})` : changes,
      by: r.proposed_by_name ?? r.proposed_by, byAgent: r.by_agent, since: r.proposed_at, canDecide: list.can_decide,
      link: `/servers/${server.id}/model/${encodeURIComponent(model)}/changes`,
    }
  })
}

/** The Gateway's `GET /api/v1/apps` and `GET /api/v1/apps/{name}`, as far as this view reads them. */
export interface GatewayAppSummary { name: string }
export interface GatewayAppDetail {
  name: string; serving: number | null
  versions: { version: number; trust: 'sandbox' | 'trusted' | null; published_by: string; published_at: string; serving: boolean; approval_id: string | null }[]
  caller: { can_approve: boolean }
}

/**
 * The versions of an app that ask to be trusted and have no approval standing for their exact files, and that matter: the one served now and any newer. An older version that is not served is history, not a request.
 * (The Gateway refuses the publisher as the approver: the row only says that it waits.)
 */
export function appRows(server: InstanceSummary, detail: GatewayAppDetail): ApprovalRow[] {
  const served = detail.serving ?? 0
  return detail.versions.filter((v) => v.trust === 'trusted' && !v.approval_id && v.version >= served).map((v) => ({
    key: `${server.id}/app/${detail.name}/${v.version}`, server: server.id, serverName: server.name, product: 'dbdatagateway', kind: 'app-trust',
    subject: `${detail.name} version ${v.version}`,
    detail: v.serving ? 'Served now and asks to be trusted, with no approval for these files: it runs sandboxed or not at all' : 'Asks to be trusted: it needs an approval for these exact files before it is served as trusted',
    by: v.published_by, byAgent: false, since: v.published_at, canDecide: detail.caller.can_approve,
    link: `/servers/${server.id}/apps`,
  }))
}

/** Oldest first: what has waited longest is at the top, and an agent's write before a person's of the same age. Rows with no date go last. */
export function sortApprovals(rows: ApprovalRow[]): ApprovalRow[] {
  const at = (r: ApprovalRow) => (r.since ? Date.parse(r.since) : Number.POSITIVE_INFINITY)
  return [...rows].sort((a, b) => (at(a) === at(b) ? 0 : at(a) < at(b) ? -1 : 1) || Number(b.byAgent) - Number(a.byAgent) || a.serverName.localeCompare(b.serverName) || a.subject.localeCompare(b.subject))
}

/** How long something has waited, in words: `under a minute`, `5 min`, `3 h`, `4 days`; empty when the time is unknown. */
export function waited(since: string | null, now: number = Date.now()): string {
  if (!since) return ''
  const minutes = Math.floor((now - Date.parse(since)) / 60000)
  if (Number.isNaN(minutes)) return ''
  if (minutes < 1) return 'under a minute'
  if (minutes < 120) return `${minutes} min`
  const hours = Math.floor(minutes / 60)
  return hours < 48 ? `${hours} h` : `${Math.floor(hours / 24)} days`
}

const relay = (server: InstanceSummary, path: string) => `/relay/${server.id}${path}`
const enc = encodeURIComponent

/**
 * Everything that waits, from every reachable Store and Gateway. A server that does not answer is reported beside the rows, never as a row, and never stops the others.
 * The decisions are made on the owning screens (the Store's Change requests tab, the Gateway's Apps screen), which show what is proposed and apply the server's own rules; this view only says that something waits.
 */
export async function loadApprovals(servers: InstanceSummary[]): Promise<{ rows: ApprovalRow[]; problems: { server: string; message: string }[] }> {
  const rows: ApprovalRow[] = []
  const problems: { server: string; message: string }[] = []
  await Promise.all(servers.filter((s) => s.reachable).map(async (server) => {
    try {
      if (server.product === 'dbdatastore') {
        const { models } = await request<{ models: { name: string }[] }>(relay(server, '/api/v1/models'))
        await Promise.all(models.map(async (m) => {
          const instances = await request<{ instances: { name: string }[] }>(relay(server, `/api/v1/models/${enc(m.name)}/instances`)).then((r) => r.instances, () => [])
          const copies = ['default', ...instances.map((i) => i.name)]
          const lists = await Promise.all(copies.map((c) => request<StoreRequestList>(relay(server, `/api/v1/models/${enc(m.name)}${c === 'default' ? '' : `/instances/${enc(c)}`}/requests?state=pending`))))
          lists.forEach((l) => rows.push(...storeRows(server, m.name, l)))
        }))
      } else if (server.product === 'dbdatagateway') {
        const { apps } = await request<{ apps: GatewayAppSummary[] }>(relay(server, '/api/v1/apps'))
        const details = await Promise.all(apps.map((a) => request<GatewayAppDetail>(relay(server, `/api/v1/apps/${enc(a.name)}`))))
        details.forEach((d) => rows.push(...appRows(server, d)))
      }
    } catch (error) {
      problems.push({ server: server.name, message: error instanceof Error ? error.message : 'No answer' })
    }
  }))
  return { rows: sortApprovals(rows), problems }
}
