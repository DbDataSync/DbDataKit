import { request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'
import { copyInfo, type BindingConnection, type RunnerCopy } from './bindings'

/** The products whose management API answers `GET /api/v1/connections` (plan 28c E23): Build, Sync and a runner. */
export const INVENTORY_PRODUCTS = ['dbdatabuild', 'dbdatasync', 'dbdatarunner'] as const
export type InventoryProduct = (typeof INVENTORY_PRODUCTS)[number]

export const hasInventory = (server: InstanceSummary): boolean => server.reachable && (INVENTORY_PRODUCTS as readonly (string | null)[]).includes(server.product)

/** One process that uses a connection, as the product reports it. */
export interface InventoryUse {
  process: string
  name: string
  version: string | null
  group: string | null
  instance: string | null
  channel: string | null
  /** `read`, `run` or `write`: the login the use needs. */
  role: string
}

/** One connection of one server: where it goes, the secret *reference* (never a value) and who uses it. */
export interface InventoryRow {
  /** Unique among the rows: the server and the name. */
  key: string
  server: string
  serverName: string
  product: InventoryProduct
  name: string
  kind: string
  engine: string | null
  host: string | null
  database: string | null
  secretRef: string | null
  roles: string[]
  usedBy: InventoryUse[]
  /** A runner's (plan 28c E27): where the definition it holds came from, `file`, `copy`, `reference` or `unknown`; null from a product that does not say. */
  source: string | null
  /** The version of the text of that definition (`sha256:<16 hex>`), the one the console computes from its file. */
  version: string | null
}

interface Answer {
  connections: { name: string; kind: string; engine: string | null; host: string | null; database: string | null; secret_ref: string | null; roles: string[]; used_by: InventoryUse[]; source?: string | null; version?: string | null }[]
}

export const productLabel: Record<InventoryProduct, string> = { dbdatabuild: 'Build', dbdatasync: 'Sync', dbdatarunner: 'Runner' }

export function inventoryRows(server: InstanceSummary, answer: Answer): InventoryRow[] {
  return answer.connections.map((c) => ({
    key: `${server.id}/${c.name}`, server: server.id, serverName: server.name, product: server.product as InventoryProduct, name: c.name, kind: c.kind,
    engine: c.engine, host: c.host, database: c.database, secretRef: c.secret_ref, roles: c.roles ?? [], usedBy: c.used_by ?? [], source: c.source ?? null, version: c.version ?? null,
  }))
}

/** By server, then by name. */
export const sortInventory = (rows: InventoryRow[]) => [...rows].sort((a, b) => a.serverName.localeCompare(b.serverName) || a.name.localeCompare(b.name))

/** The connections of every paired server that reports them, each asked through the relay. A server that does not answer (an older one has no such call) is named in `problems`; the others still show. */
export async function loadInventory(servers: InstanceSummary[]): Promise<{ rows: InventoryRow[]; problems: { server: string; message: string }[] }> {
  const rows: InventoryRow[] = []
  const problems: { server: string; message: string }[] = []
  await Promise.all(servers.filter(hasInventory).map(async (server) => {
    try {
      rows.push(...inventoryRows(server, await request<Answer>(`/relay/${encodeURIComponent(server.id)}/api/v1/connections`)))
    } catch (error) {
      problems.push({ server: server.name, message: error instanceof Error ? error.message : 'No answer' })
    }
  }))
  return { rows: sortInventory(rows), problems }
}

/** How a user reads in a line: "task orders/extract v3 on main/7f1 (prod), run login". */
export function describeUse(use: InventoryUse): string {
  const where = use.instance ? ` on ${use.group ?? 'runner'}/${use.instance}` : use.group ? ` in ${use.group}` : ''
  const version = use.version ? ` v${use.version}` : ''
  const channel = use.channel ? ` (${use.channel})` : ''
  return `${use.process} ${use.name}${version}${where}${channel}, ${use.role} login`
}

export interface InventoryFilter {
  /** "Where is this connection used?": part of a connection's name. */
  connection: string
  /** "Exists on": part of the name of the server that holds the connection. */
  server?: string
  /** "Used by" ("what does this task connect to?"): part of the name of a task, plan, replication or flow that uses it. */
  user: string
}

const has = (text: string, part: string) => text.toLowerCase().includes(part.trim().toLowerCase())

/** The rows that match: a connection by name, and/or those with a user of that name. With a user filter, each row keeps only the users that match, so the answer reads as the task's own list. */
export function filterInventory(rows: InventoryRow[], filter: InventoryFilter): InventoryRow[] {
  return rows
    .filter((r) => filter.connection.trim() === '' || has(r.name, filter.connection))
    .filter((r) => (filter.server ?? '').trim() === '' || has(r.serverName, filter.server ?? ''))
    .map((r) => (filter.user.trim() === '' ? r : { ...r, usedBy: r.usedBy.filter((u) => has(u.name, filter.user)) }))
    .filter((r) => filter.user.trim() === '' || r.usedBy.length > 0)
}

/** Where a connection goes, in a few words: host and database, the engine alone, or what the product knows. */
export const addressOf = (row: InventoryRow): string => (row.host ? `${row.host}${row.database ? `/${row.database}` : ''}` : row.engine ?? row.kind)

/** Whether a connection can be removed from the console: only a Sync server removes connections over its API; the server decides, and refuses one a replication uses. */
export const canRemove = (row: InventoryRow): boolean => row.product === 'dbdatasync'

export const removeConnection = (row: InventoryRow) => request<unknown>(`/relay/${encodeURIComponent(row.server)}/api/connections/${encodeURIComponent(row.name)}`, { method: 'DELETE' })

/**
 * Where a runner's definition of a connection stands against the console's file (plan 28c E27, ED17), beside the use: `current` (the runner's copy is the console's file as it is now), `stale` (the console's file changed since it was sent), `missing` (the console has
 * the file and the runner has no copy: a run bound to it would fail), `runner's own` (a file of the runner's own folder answers first, or a name or reference the console does not keep). It is the Bindings page's comparison (`copyInfo`, the version both sides compute from
 * the text), not a second one. Null for a product that has no copies (Build, Sync) and for a name nobody holds.
 */
export type RunnerCopyState = 'current' | 'stale' | 'missing' | "runner's own"

export function runnerCopyState(row: InventoryRow, consoleConnections: BindingConnection[]): RunnerCopyState | null {
  if (row.product !== 'dbdatarunner' || row.source === null) return null
  // a file of the runner's own answers to the name first, whatever the console holds
  if (row.source === 'file') return "runner's own"
  const copies = row.source === 'copy' && row.version ? ([{ name: row.name, version: row.version }] as RunnerCopy[]) : []
  const state = copyInfo(row.name, consoleConnections, copies).state
  if (state === 'current' || state === 'stale' || state === 'missing') return state
  // the console keeps no such name: a reference, or a copy of a connection the console no longer keeps, is the runner's own; a bound name nobody holds has nothing to compare
  return row.source === 'unknown' ? null : "runner's own"
}
