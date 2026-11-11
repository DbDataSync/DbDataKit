/** What `POST /api/v1/my/runs` answers (plan 14a SL14, D36): the engine's result as the REST route reads it; an explain's rows are its scans (status `explained`). */
export interface RunResult {
  status: 'ok' | 'explained' | 'unsupported'
  columns: { name: string; type: string }[]
  rows: unknown[][]
  warnings: string[]
  request_hash: string | null
  /** What the engine left (steps this host does not run), when the result is partial. */
  remaining?: unknown
  reason?: string
  model_version?: number | null
}

/** One record of a statement (SL23). `caller` is the person's key: the email or UPN (D35). */
export interface RunRow {
  id: number
  caller: string
  model: string
  model_version: number | null
  kind: string
  text: string
  status: string
  error_code: string | null
  request_hash: string | null
  started_at: string
  duration_ms: number
  rows: number
  replay_of: number | null
}
export interface RunDetail extends RunRow { message: string | null; request: Record<string, unknown> | null; warnings: unknown; plan: string | null }
export interface ReplayResult extends RunResult { replay_of: number; run_id: number; same_hash: boolean }

/** A saved query as the Gateway lists it (SL17). `owner` is the email or UPN (D35), `owner_name` the display name as of the last save. */
export interface SavedQueryRow {
  model: string
  name: string
  owner: string
  owner_name?: string | null
  own: boolean
  version?: number
  description: string | null
  sql: string | null
  request: Record<string, unknown> | null
  parameters: Record<string, unknown>
  updated_at?: string
}
export interface SavedQueryVersionRow { version: number; saved_by: string; saved_at: string; description: string | null; sql: string | null; request: Record<string, unknown> | null; parameters: Record<string, unknown> }
