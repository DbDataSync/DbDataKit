import type { RenderedViewDocument } from '@dbdatakit/visuals'

/** What `POST /api/v1/my/runs` answers (plan 14a SL14, D36): the engine's result as the REST route reads it; an explain's rows are its scans (status `explained`). */
export interface RunResult {
  status: 'ok' | 'explained' | 'unsupported'
  /** `binding` is the model member a column is (a result drawn through the renderer carries it). */
  columns: { name: string; type: string; binding?: string }[]
  rows: unknown[][]
  warnings: string[]
  request_hash: string | null
  /** What the engine left (steps this host does not run), when the result is partial. */
  remaining?: unknown
  reason?: string
  model_version?: number | null
  /** The kept result a view of it is drawn from (a run that asked `keep` or sent a `view`); see `runs.render`. */
  result_id?: string
  /** What the Gateway says beside the rows: `view_unavailable` names the column whose type matrix and chart cannot read, so the table is all there is (plan 14a D38). */
  notices?: { code: string; message: string; column?: string; type?: string }[]
  /** The rollup levels of a result with totals, and the attributes each rolled up (logical names). */
  totals?: { level_field: string; levels: Array<{ level: number; rolled_up: string[] }> }
  /** What the Gateway's renderer made of the `view` that was sent: the matrix or chart, or the engine's errors (plan 14a D37). */
  rendered_view?: RenderedViewDocument
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
