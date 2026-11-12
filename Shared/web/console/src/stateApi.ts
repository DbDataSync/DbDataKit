import { request } from '@dbdatakit/shell'

/** A module the registry of a state database lists. */
export interface FoundModule { module: string; scope: string; version: number }

/** A paired server that uses a state database. */
export interface StateUser { id: string; name: string; product: string }

/** A state database the console knows by name (plan 29b, C06): a connection of kind `state`. */
export interface StateConnectionRow {
  name: string
  engine: 'postgres' | 'mssql' | 'sqlite'
  /** The connection string with the password as the secret reference, or the SQLite file. */
  connection: string | null
  note: string | null
  /** The name of the secret that holds the password, in this console's store and in any server's. */
  secret: string | null
  reachable: boolean
  empty: boolean
  ok: boolean
  /** One sentence: what is fine, or the fix. */
  sentence: string
  modules: FoundModule[]
  usedBy: StateUser[]
}

export interface StateTestStep { name: string; status: string; message: string }

export interface StateTestResult { ok: boolean; reachable: boolean; empty: boolean; sentence: string; steps: StateTestStep[]; modules: FoundModule[] }

export interface SaveStateConnection { engine: string; connection: string | null; path: string | null; password: string | null; note: string | null }

export interface PreflightCheck { name: string; ok: boolean; sentence: string }
export interface Preflight { ok: boolean; checks: PreflightCheck[]; program: string | null; old?: boolean; maintenance?: Maintenance }

export interface Maintenance { enabled: boolean; idle: boolean; since: string | null; reason: string | null; inflight: number }

export interface StateModuleRow { module: string; scope: string | null; binary_version: number; registered_version: number | null; pending: number }

export interface StateSource { kind: string; name: string | null }

export interface StateInstanceRow { key: string; self: boolean; host?: string; process_id?: number; started?: string; heartbeat?: string; software_version?: string }

/** What `GET /api/v1/state` of a server answers (plan 29b, C05). */
export interface ServerState {
  product: string
  software_version: string | null
  management_api_version: number
  read: boolean
  error?: string
  engine?: string
  connection?: string
  file?: string
  pending_migrations?: number
  modules?: StateModuleRow[]
  sources?: Record<string, StateSource>
  instances?: StateInstanceRow[]
  maintenance: Maintenance
}

export interface MoveTable { step: string; module: string; table: string; rows?: number; done: boolean }

/** What `GET /api/v1/state/move` of a server answers while it moves its own state. */
export interface MoveProgress {
  state: 'none' | 'running' | 'succeeded' | 'failed' | 'preflight_ok'
  step?: string
  message?: string
  to_engine?: string
  to_connection?: string
  rows?: number
  tables_done?: number
  tables_total?: number
  tables?: MoveTable[]
  steps?: { step: string; at: string; message: string }[]
  error?: string
  kept?: string
  settings_changed?: boolean
  restart?: string
  way_back?: string
  settings_file?: string
  backup_file?: string
  summary?: string
}

const enc = encodeURIComponent
/** Where a server's state endpoints are: through the relay for a paired server, direct for the console itself. */
export const stateBase = (instance: string) => (instance === 'console' ? '' : `/relay/${enc(instance)}`)

export const stateApi = {
  connections: {
    list: () => request<StateConnectionRow[]>('/api/state-connections'),
    save: (name: string, body: SaveStateConnection) => request<{ name: string }>(`/api/state-connections/${enc(name)}`, { method: 'PUT', body: JSON.stringify(body) }),
    remove: (name: string) => request<void>(`/api/state-connections/${enc(name)}`, { method: 'DELETE' }),
    test: (name: string) => request<StateTestResult>(`/api/state-connections/${enc(name)}/test`, { method: 'POST' }),
  },
  server: {
    state: (instance: string) => request<ServerState>(`${stateBase(instance)}/api/v1/state`),
    maintenance: (instance: string, enabled: boolean, reason: string | null) =>
      request<Maintenance>(`${stateBase(instance)}/api/v1/state/maintenance`, { method: 'POST', body: JSON.stringify({ enabled, reason }) }),
    maintenanceStatus: (instance: string) => request<Maintenance>(`${stateBase(instance)}/api/v1/state/maintenance`),
  },
  move: {
    preflight: (server: string, connection: string) => request<Preflight>('/api/state-moves/preflight', { method: 'POST', body: JSON.stringify({ server, connection }) }),
    start: (server: string, connection: string) => request<MoveProgress>('/api/state-moves/start', { method: 'POST', body: JSON.stringify({ server, connection }) }),
    progress: (server: string) => request<MoveProgress>(`/api/state-moves/${enc(server)}`),
  },
}

/** The products whose state a console moves: the ones that mount the move call. */
export const MOVABLE_PRODUCTS = ['dbdatagateway', 'dbdatastore', 'dbdatabuild', 'dbdatarunner'] as const
export const offersState = (product: string | null) => (MOVABLE_PRODUCTS as readonly (string | null)[]).includes(product)

/** The engine as a person says it. */
export const engineName = (engine: string | undefined) => ({ postgres: 'PostgreSQL', mssql: 'SQL Server', sqlite: 'SQLite' } as Record<string, string>)[(engine ?? '').toLowerCase()] ?? engine ?? '?'

/** The module versions of a state connection in one line: `identity 3, audit 1`. */
export const versionsLine = (modules: FoundModule[]) => (modules.length === 0 ? 'none' : modules.map((m) => `${m.module} ${m.version}`).join(', '))

/** Whether every module is where this binary is: nothing pending and nothing newer. */
export const modulesCurrent = (modules: StateModuleRow[]) => modules.every((m) => m.registered_version === m.binary_version)
