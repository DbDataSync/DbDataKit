import { request } from '@dbdatakit/shell'
import { readCatalogCache, tableLabel, writeCatalogCache, type CacheColumn, type CacheConnection, type CatalogCache } from './catalogCache'

// The read of a connection's shape from a Runner channel, as a Workspace screen asks the console for it, and what the screen does with the answer:
// the difference with the cache it holds (unsaved edits included) and the cache with the new entry in place.
// Design: docs/authoring-metadata.md#how-a-screen-asks-for-a-read

/** A paired Runner the console lists (`/api/instances`): its id in the console and its name. */
export interface RunnerChoice { id: string; name: string }

/** One channel of the project's package on a Runner: whether it is production, and whether it binds the connection's slot (and to what). */
export interface ChannelChoice { channel: string; production: boolean; bound: boolean; bound_to: string | null }

/** What the console answers for the channel picker. `default` is the first channel that is not production and binds the slot; `note` says how to bind one when none does. */
export interface ChannelList { package: string; connection: string; slot: string; runner: string; runner_name: string; channels: ChannelChoice[]; default: string | null; note?: string }

/**
 * Who reads a connection on the Runner: its built-in package (`builtin`, the default), or the project's own package (`package`), whose tasks `discover` and
 * `describe` read a connection whose driver the built-in package cannot run, in the newest build the channel holds.
 */
export type CatalogReader = 'builtin' | 'package'

/** What a read is asked: the Runner, the channel, the project's connection, one schema or one table of it, and who reads it. */
export interface CatalogReadRequest { runner: string; channel: string; connection: string; schema?: string; table?: string; reader?: CatalogReader }

/** Where a read stands: still going (with the run's link), or done with the connection as a `catalog-cache/2` file of that one connection. */
export interface CatalogReadAnswer {
  state: 'done' | 'running' | 'failed'
  run_id: string
  link: string
  runner: string
  runner_name: string
  channel: string
  production: boolean
  connection: string
  cache?: string
  tables?: number
  columns?: number
  error?: string
}

/** What a read from a Gateway is asked: the Gateway, the project's connection, the Gateway's data source for it (the connection's name when none is given), and `adopt` once the person confirmed a data source made by hand. */
export interface GatewayReadRequest { gateway: string; connection: string; data_source?: string; adopt?: boolean }

/** What a read from a Gateway answers: the connection as a `catalog-cache/2` file of that one connection, recorded as read from the Gateway. */
export interface GatewayReadAnswer { state: 'done'; gateway: string; gateway_name: string; connection: string; data_source: string; cache: string; tables: number; columns: number }

const enc = encodeURIComponent
const base = (workspace: string, project: string) => `/api/workspace-catalog/${enc(workspace)}/${enc(project)}`

/** The reader in a follow's query: nothing for the built-in package, the default. */
export const readerQuery = (reader: CatalogReader | undefined) => (reader && reader !== 'builtin' ? `&reader=${enc(reader)}` : '')

/** The servers of one product the console lists (`/api/instances`): each one's id in the console and its name. */
const serversOf = (product: string) => () =>
  request<{ id: string; name: string; product: string | null }[]>('/api/instances').then((all) => all.filter((s) => s.product === product).map((s) => ({ id: s.id, name: s.name })))

/** The console's routes of a read. They are the console's own, whatever server the module talks to (`HOST_OWN` in the shell). */
export const catalogReadApi = {
  runners: serversOf('dbdatarunner'),
  gateways: serversOf('dbdatagateway'),
  gatewayRead: (workspace: string, project: string, body: GatewayReadRequest) =>
    request<GatewayReadAnswer>(`${base(workspace, project)}/gateway-read`, { method: 'POST', body: JSON.stringify(body) }),
  channels: (workspace: string, project: string, runner: string, connection: string) =>
    request<ChannelList>(`${base(workspace, project)}/channels?runner=${enc(runner)}&connection=${enc(connection)}`),
  read: (workspace: string, project: string, body: CatalogReadRequest) =>
    request<CatalogReadAnswer>(`${base(workspace, project)}/read`, { method: 'POST', body: JSON.stringify(body) }),
  follow: (workspace: string, project: string, run: string, body: CatalogReadRequest) =>
    request<CatalogReadAnswer>(`${base(workspace, project)}/read/${enc(run)}?runner=${enc(body.runner)}&channel=${enc(body.channel)}&connection=${enc(body.connection)}${readerQuery(body.reader)}`),
}

/** The Workspace server a module talks to through the console's relay (`/relay/<id>`), or null when the screens are not in a console. */
export function workspaceServerOf(apiBase: string): string | null {
  const m = /^\/relay\/([^/]+)\/?$/.exec(apiBase)
  return m ? decodeURIComponent(m[1]!) : null
}

/**
 * The channel the picker offers first: the one the cache says the connection was read in (`readIn`), when the Runner still binds the slot there; else
 * the console's `default`, the first channel that is not production and binds it; else none (a channel that binds nothing is not offered by itself).
 */
export const defaultChannel = (list: ChannelList | undefined, readIn?: string | null): string =>
  (readIn && list?.channels.some((c) => c.channel === readIn && c.bound) ? readIn : list?.default) ?? ''

/** The Runner the picker offers first: the one the cache says the connection was read from (by its name in the console), else the first one listed. */
export const defaultRunner = (runners: readonly RunnerChoice[] | undefined, readFrom?: string | null): string =>
  runners?.find((r) => readFrom !== undefined && readFrom !== null && r.name === readFrom)?.id ?? runners?.[0]?.id ?? ''

/** Where the cache says a connection was last read from a Runner: its server's name and the channel, or null. */
export function lastRunnerRead(current: string | undefined, connection: string): { server: string; channel: string | null } | null {
  if (current === undefined) return null
  const read = readCatalogCache(current).cache?.connections[connection]?.read
  return read && read.product === 'runner' ? { server: read.server, channel: read.channel } : null
}

/** The sentence shown when the person picks production: the read goes ahead after it. */
export const productionWarning = (connection: string, channel: string, runner: string) =>
  `This reads production: the shape of ${connection} as the ${channel} channel of ${runner} binds it, with production's login. Only names and types are read, never a row.`

/** What a channel without a binding of the slot needs, in the picker: Set up for development binds it, production as any other channel. */
export const unboundLine = (choice: ChannelChoice, slot: string) =>
  `${choice.channel} does not bind the slot ${slot}. Set up for development binds it with a login you type, or an administrator binds it on the Runner's Packages page (Bindings), then the read can be asked.`

// ---- the difference a refresh makes

export interface ColumnChange { table: string; column: string; from?: string; to?: string }

/** What a refresh changes in a connection's entry: tables and columns added and removed, columns whose type or nullability changed. */
export interface CatalogDifference { tablesAdded: string[]; tablesRemoved: string[]; columnsAdded: ColumnChange[]; columnsRemoved: ColumnChange[]; columnsRetyped: ColumnChange[] }

const shapeOf = (c: CacheColumn) => `${c.type}${c.nullable ? '' : ' not null'}`

/** One table of a connection by schema and name (the database is the one the connection opens). */
const sameTable = (t: { schema: string; name: string }, wanted: { schema: string; name: string }) => t.schema === wanted.schema && t.name === wanted.name

/**
 * The difference between the entry the screen holds and the one a read answered. With `table`, only that table is compared (a read of one table answers that
 * one; it is removed when the read no longer finds it).
 */
export function refreshDifference(before: CacheConnection | undefined, after: CacheConnection, table?: { schema: string; name: string }): CatalogDifference {
  const pick = (c: CacheConnection | undefined) => (c?.tables ?? []).filter((t) => table === undefined || sameTable(t, table))
  const old = new Map(pick(before).map((t) => [tableLabel(t), t]))
  const now = new Map(pick(after).map((t) => [tableLabel(t), t]))
  const d: CatalogDifference = { tablesAdded: [], tablesRemoved: [], columnsAdded: [], columnsRemoved: [], columnsRetyped: [] }
  for (const [label, t] of now) {
    const was = old.get(label)
    if (!was) { d.tablesAdded.push(label); continue }
    const wasColumns = new Map(was.columns.map((c) => [c.name, c]))
    const nowColumns = new Map(t.columns.map((c) => [c.name, c]))
    for (const c of t.columns) {
      const prior = wasColumns.get(c.name)
      if (!prior) d.columnsAdded.push({ table: label, column: c.name, to: shapeOf(c) })
      else if (shapeOf(prior) !== shapeOf(c)) d.columnsRetyped.push({ table: label, column: c.name, from: shapeOf(prior), to: shapeOf(c) })
    }
    for (const c of was.columns) if (!nowColumns.has(c.name)) d.columnsRemoved.push({ table: label, column: c.name, from: shapeOf(c) })
  }
  for (const label of old.keys()) if (!now.has(label)) d.tablesRemoved.push(label)
  return d
}

export const isUnchanged = (d: CatalogDifference) =>
  d.tablesAdded.length + d.tablesRemoved.length + d.columnsAdded.length + d.columnsRemoved.length + d.columnsRetyped.length === 0

const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`

/** The difference in one line: `1 column added`, `2 tables added, 1 column retyped`, `no change`. */
export function differenceLine(d: CatalogDifference): string {
  const parts: string[] = []
  if (d.tablesAdded.length) parts.push(`${count(d.tablesAdded.length, 'table', 'tables')} added`)
  if (d.tablesRemoved.length) parts.push(`${count(d.tablesRemoved.length, 'table', 'tables')} removed`)
  if (d.columnsAdded.length) parts.push(`${count(d.columnsAdded.length, 'column', 'columns')} added`)
  if (d.columnsRemoved.length) parts.push(`${count(d.columnsRemoved.length, 'column', 'columns')} removed`)
  if (d.columnsRetyped.length) parts.push(`${count(d.columnsRetyped.length, 'column', 'columns')} retyped`)
  return parts.length ? parts.join(', ') : 'no change'
}

/**
 * The cache with a read's entry in place. A read of the whole connection replaces the connection; a read of one table replaces that table (or removes
 * it when the read no longer finds it) and keeps the connection's other tables and its databases. The other connections stay as they are.
 */
export function withRefresh(cache: CatalogCache | null, connection: string, after: CacheConnection, generatedAt: string, table?: { schema: string; name: string }): CatalogCache {
  const connections = { ...cache?.connections }
  const old = connections[connection]
  if (table === undefined || old === undefined) connections[connection] = after
  else {
    const kept = old.tables.filter((t) => !sameTable(t, table))
    connections[connection] = { ...old, engine: old.engine !== 'unknown' ? old.engine : after.engine, read: after.read, tables: [...kept, ...after.tables.filter((t) => sameTable(t, table))] }
  }
  return { generatedAt, connections }
}

/** What a finished read gives the screen: the entry, the difference with what it holds, and the text of the cache once accepted; or why it cannot be accepted. */
export type RefreshOutcome =
  | { ok: true; entry: CacheConnection; difference: CatalogDifference; text: string }
  | { ok: false; problem: string }

/**
 * A finished read against the cache text the screen holds (`current`: the unsaved edit when there is one, undefined when the draft has no cache). A cache
 * the strict reader refuses (a file of format 1, a damaged file) is not replaced by a refresh of one connection: the person deletes it or fixes it first,
 * so no connection is dropped without being seen.
 */
export function refreshOutcome(current: string | undefined, answer: string, connection: string, table?: { schema: string; name: string }): RefreshOutcome {
  const held = current === undefined ? null : readCatalogCache(current)
  if (held && !held.cache) return { ok: false, problem: `The draft's catalog cache cannot be read (${held.findings[0]?.message ?? 'not a catalog cache'}). Delete the file or fix it, then refresh again.` }
  const read = readCatalogCache(answer)
  const entry = read.cache?.connections[connection]
  if (!read.cache || !entry) return { ok: false, problem: `The answer of the read is not a catalog of ${connection} (${read.findings[0]?.message ?? 'no such connection'}).` }
  const difference = refreshDifference(held?.cache?.connections[connection], entry, table)
  return { ok: true, entry, difference, text: writeCatalogCache(withRefresh(held?.cache ?? null, connection, entry, read.cache.generatedAt, table)) }
}
