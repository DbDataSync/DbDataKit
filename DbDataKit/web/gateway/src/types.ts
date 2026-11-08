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
/** The gateway's answer to a hand-over: `adopted` is false when the data source already was the client's. */
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

// ---- Apps (plan 26, AP24): what the Gateway's /api/v1/apps, /app-instances, /app-navigation and /app-approvals answer ----

/** What the caller may do, as the Gateway decided it from their roles on this server; the screen only explains it (the Gateway enforces it on every call). */
export interface AppsCaller { can_publish: boolean; can_approve: boolean; you: string }
export interface AppSummary {
  name: string; version: number; versions: number[]; title: string | null; manifest_version: string | null; description: string | null
  kind: 'app' | 'portal' | null; trust: 'sandbox' | 'trusted' | null; published_by: string; published_at: string; instances: string[]
}
export interface AppAccess { model: string; entity: string; operations: string }
export interface AppEgressRow { directive: string; origins: string[] }
/** The "about" data of a version (AP18): what its manifest declares it can reach, who published it and which approval stands for these bytes. */
export interface AppAbout {
  title: string | null; description: string | null; kind: string; trust: 'sandbox' | 'trusted'; roles: string[]; models: AppAccess[]; semantic_models: string[]; egress: AppEgressRow[]
  storage: { user: boolean; instance: boolean; quota_mb: number } | null; published_by: string; published_at: string
  approval: { id: string; approved_by: string; approved_at: string; note: string | null } | null
}
export interface AppVersionRow {
  version: number; manifest_version: string | null; trust: 'sandbox' | 'trusted' | null; published_by: string; published_at: string; note: string | null
  serving: boolean; hash: string; approval_id: string | null; files: number; about: AppAbout | null
}
export interface AppDetail { name: string; serving: number | null; versions: AppVersionRow[]; caller: AppsCaller }
export interface AppInstanceRow {
  name: string; app: string; title: string | null; pin: number | null; follows: boolean; instance_version: number; published_by: string; published_at: string
  path: string; address: string | null; bindings: Record<string, string>; settings: Record<string, string>; navigation: string[]; mounted_by: string[]
  /** The version of the app the instance runs, and what the app serves now; absent when the instance's app is gone (then `problem` says so). */
  app_version?: number; serving_version?: number | null; manifest_version?: string; kind?: 'app' | 'portal'; trust?: 'sandbox' | 'trusted'
  runs_as_asked?: boolean; approval_id?: string | null; mounts?: { instance: string; path: string }[]; problem?: string
}
export interface AppInstances { instances: AppInstanceRow[]; apps_address: string | null; caller: AppsCaller }
export interface NavigationSetRow { name: string; title: string; entries: { instance: string; label: string | null; icon: string | null }[] }
export interface AppApprovalRow {
  id: string; app: string; version: string; hash: string; approved_by: string; approved_at: string; note: string | null; published_version: number | null; revoked: boolean; revoked_at: string | null
  operations: AppAccess[]; egress: AppEgressRow[]
}
/** What the Gateway's `POST /api/v1/apps/check` says of files before they are published: the manifest's own diagnostics, what the version would be, and whether an approval already stands for these exact files. */
export interface AppCheckDiagnostic { code: string; severity: string; file: string; line: number; column: number; message: string; fix: string | null }
export interface AppCheck {
  ok: boolean; diagnostics: AppCheckDiagnostic[]; files: number
  name: string | null; manifest_version: string | null; trust: 'sandbox' | 'trusted' | null; hash: string | null; approval_id: string | null; next_version: number | null; about: AppAbout | null
}
export interface AppApprovals { approvals: AppApprovalRow[]; caller: AppsCaller }
export interface AppsList { apps: AppSummary[]; caller: AppsCaller }
