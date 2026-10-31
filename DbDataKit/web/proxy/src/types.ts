export interface Overview {
  product: string
  version: string
  instance_id: string
  started: string
  uptime_seconds: number
  listen: string
  data_sources: number
  users: number
  windows_accounts: number
  pass_through: boolean
  jwt: boolean
  sessions: number
  running_queries: number
  waiting_for_a_slot: number
  refused_for_want_of_a_slot_total: number
  locked_out_clients: number
  failures_last_hour: number
}

export interface DataSourceSummary { name: string; version: string; valid: boolean; engine: string | null; access: string | null; managed_by?: string | null }
/** A paired server client (a Build server) a data source can be handed to (T13). */
export interface ServerClient { id: string; name: string }
/** The proxy's answer to a hand-over: `adopted` is false when the data source already was the client's. */
export interface AdoptAnswer { name: string; managed_by: string; adopted: boolean; previous_owner?: string | null }
export interface DataSourceFile { name: string; text: string; version: string }
export interface Diagnostic { code: string; severity: string; key: string | null; file: string; line: number; column: number; message: string; fix: string | null }
export interface WriteDataSourceResult { name: string; version: string; created: boolean; diagnostics: Diagnostic[] }
export interface TestResult { ok: boolean; message?: string; kind?: string; code?: string; elapsed_ms?: number }

export interface UserRow { name: string; auth_type: string; has_password: boolean; data_sources: string[]; default_data_source: string | null; version: string }
export interface JwtMapping { claim: string; value: string; data_sources: string[]; default_data_source: string | null }
export interface Identities {
  users: UserRow[]
  windows_accounts: UserRow[]
  pass_through: boolean
  jwt: { enabled: boolean; authority: string | null; issuer: string | null; audience: string | null; name_claim: string; clock_skew_seconds: number; has_signing_key: boolean; mappings: JwtMapping[] }
  sessions: { idle_minutes: number; max_hours: number; max_failed_logins: number; lockout_minutes: number }
  auth_version: string | null
}

export interface CertificateInfo {
  mode: string
  present: boolean
  subject?: string
  issuer?: string
  self_signed?: boolean
  thumbprint?: string
  not_before?: string
  not_after?: string
  days_left?: number
  needs_renewal?: boolean
  renew_before_days: number
  names?: string[]
  renewed?: boolean
  warning?: string
}

export interface SessionRow { id: string; user: string; method: string; data_sources: string[]; last_used: string; expires: string }
export interface QueryRow { id: string; user: string; data_source: string; kind: string; started: string; running_seconds: number; sql: string | null }
export interface LimitRow { data_source: string; max_concurrent_queries: number; max_concurrent_queries_per_user: number; acquire_timeout_seconds: number; limited: boolean; active: number; waiting: number; entered_total: number; refused_total: number }
export interface Limits { data_sources: LimitRow[]; login_throttle: { max_failed_logins: number; lockout_minutes: number; held: { client: string; user: string; failures: number; locked_until: string | null }[] } }
export interface FailureRow { at: string; data_source: string; kind: string; code: string | null; engine: string | null; message: string }
export interface NotificationRow { id: number; kind: string; created: string; message: string }
export interface CommitRow { sha: string; message: string; author: string; email: string; when: string }
export interface CommitDiff { sha: string; message: string; truncated: boolean; changes: { path: string; kind: string; before: string | null; after: string | null }[] }
