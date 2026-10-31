import { ApiError, request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'

/** The products whose servers keep connections the console lists and copies between: a Sync server's connections and a Proxy's data sources are the same `connection/1` file. */
export const CONNECTION_PRODUCTS = ['dbdatasync', 'dbdataproxy'] as const
export type ConnectionProduct = (typeof CONNECTION_PRODUCTS)[number]

export const keepsConnections = (server: InstanceSummary): boolean => server.reachable && (CONNECTION_PRODUCTS as readonly (string | null)[]).includes(server.product)

/** One connection of one server, as a line of the view across servers. */
export interface ConnectionRow {
  /** Unique among the rows: the server and the name. */
  key: string
  server: string
  serverName: string
  product: ConnectionProduct
  name: string
  engine: string
  /** Where it connects, or how it may be used, in a few words. */
  detail: string
  /** False for a file the server cannot read (it can be listed, not copied). */
  valid: boolean
}

interface SyncConnection { name: string; driverType: string; host: string | null; port: number | null; database: string | null; connectionString: string | null }
interface ProxyDataSource { name: string; engine: string | null; access: string | null; valid: boolean; managed_by: string | null }

const enc = encodeURIComponent
const relay = (server: InstanceSummary, path: string) => `/relay/${enc(server.id)}${path}`

/** A Sync connection: its driver, and its address (host, port and database, or "connection string"; the string itself can hold what should not be shown). */
export function syncConnectionRow(server: InstanceSummary, c: SyncConnection): ConnectionRow {
  const address = c.host ? `${c.host}${c.port ? `:${c.port}` : ''}${c.database ? `/${c.database}` : ''}` : c.connectionString ? 'connection string' : 'no address'
  return { key: `${server.id}/${c.name}`, server: server.id, serverName: server.name, product: 'dbdatasync', name: c.name, engine: c.driverType, detail: address, valid: true }
}

/** A Proxy data source: its engine, whether it can write, and who manages it. */
export function proxyConnectionRow(server: InstanceSummary, d: ProxyDataSource): ConnectionRow {
  const owner = d.managed_by ? `managed by ${d.managed_by}` : 'by hand'
  return {
    key: `${server.id}/${d.name}`, server: server.id, serverName: server.name, product: 'dbdataproxy', name: d.name, engine: d.engine ?? 'unreadable',
    detail: d.valid ? `${d.access ?? 'readwrite'}, ${owner}` : 'the file is not valid', valid: d.valid,
  }
}

/** By server, then by name, so a connection is where a person looks for it. */
export const sortConnections = (rows: ConnectionRow[]) => [...rows].sort((a, b) => a.serverName.localeCompare(b.serverName) || a.name.localeCompare(b.name))

/**
 * The connections of every server that keeps them, each asked through the relay (as the monitoring view does). A server that does not answer, or whose list the server refuses (a Sync repository that still holds a connection in the form of earlier releases says so,
 * naming the file), is named in `problems`, and the others still show.
 */
export async function loadConnections(servers: InstanceSummary[]): Promise<{ rows: ConnectionRow[]; problems: { server: string; message: string }[] }> {
  const rows: ConnectionRow[] = []
  const problems: { server: string; message: string }[] = []
  await Promise.all(servers.filter(keepsConnections).map(async (server) => {
    try {
      if (server.product === 'dbdatasync') {
        rows.push(...(await request<SyncConnection[]>(relay(server, '/api/connections'))).map((c) => syncConnectionRow(server, c)))
      } else {
        const { data_sources } = await request<{ data_sources: ProxyDataSource[] }>(relay(server, '/api/v1/data-sources'))
        rows.push(...data_sources.map((d) => proxyConnectionRow(server, d)))
      }
    } catch (error) {
      problems.push({ server: server.name, message: error instanceof Error ? error.message : 'No answer' })
    }
  }))
  return { rows: sortConnections(rows), problems }
}

/** A fault a server found in the file it was sent, with the line it is on. */
export interface CopyDiagnostic { code: string; severity: string; line: number; message: string; fix?: string | null }

export interface CopyDone { from: string; to: string; name: string; notes: string[]; secrets: string[]; version: string | null; diagnostics: CopyDiagnostic[] }

/** Why a copy was not made: the sentence, what the target found wrong in the file, and what the console had changed for it. */
export interface CopyFailure { message: string; status: number; diagnostics: CopyDiagnostic[]; notes: string[] }

/** What a refused copy's answer says (the console passes on the target's status, sentence and diagnostics). */
export function copyFailure(error: unknown): CopyFailure {
  if (error instanceof ApiError) {
    const body = (error.body ?? {}) as { diagnostics?: CopyDiagnostic[] | null; notes?: string[] | null }
    return { message: error.message, status: error.status, diagnostics: body.diagnostics ?? [], notes: body.notes ?? [] }
  }
  return { message: error instanceof Error ? error.message : 'No answer', status: 0, diagnostics: [], notes: [] }
}

export interface CopyRequest { from: string; name: string; to: string; newName: string; password: string }

/** A copy, made by the console: it reads the file from one server and writes it to the other as the signed-in person; the target's own reading of the file is the validation. */
export const copyConnection = (r: CopyRequest) =>
  request<CopyDone>('/api/connections/copy', { method: 'POST', body: JSON.stringify({ from: r.from, name: r.name, to: r.to, newName: r.newName.trim() || null, password: r.password || null }) })

/** Whether a copy can be tried: the target is chosen, and it is not the same name on the same server. */
export const canCopy = (row: ConnectionRow, target: string, newName: string) => target !== '' && !(target === row.server && (newName.trim() === '' || newName.trim() === row.name))
