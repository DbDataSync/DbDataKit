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

/** Who may use a data source (plan 22a GP03): `definition` when a published definition says it (the project's grants, replaced by each publish), `file` for a file made by hand. Shown, never edited, on a published one. */
export interface DataSourceGrants { groups: string[]; everyone: boolean; source: 'definition' | 'file' }
/** The serving policy of a data source and whose it is (plan 13c D19): `definition` when a client publishes it (the next publish replaces `fields` whole), `file` for a file made by hand. `blocked` holds `allow_writes` and `allowed_directories`, which only an administrator sets in the data source file on the Gateway's disk. Shown, never edited. */
export interface DataSourcePolicy { origin: 'definition' | 'file'; fields: Record<string, string>; blocked: Record<string, string> }
export interface DataSourceSummary { name: string; version: string; valid: boolean; engine: string | null; access: string | null; managed_by?: string | null; grants?: DataSourceGrants | null; policy?: DataSourcePolicy | null }
/** A paired server client (a Build server) a data source can be handed to (T13). */
export interface ServerClient { id: string; name: string }
/** The gateway's answer to a hand-over: `adopted` is false when the data source already was the client's. */
export interface AdoptAnswer { name: string; managed_by: string; adopted: boolean; previous_owner?: string | null }
export interface DataSourceFile { name: string; text: string; version: string; grants?: DataSourceGrants | null; policy?: DataSourcePolicy | null }
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
  /** The names and paths the instance is also served at (plan 26 AP08); absent from a Gateway that has none. */
  addresses?: AppAddressRow[]
  /** The version of the app the instance runs, and what the app serves now; absent when the instance's app is gone (then `problem` says so). */
  app_version?: number; serving_version?: number | null; manifest_version?: string; kind?: 'app' | 'portal'; trust?: 'sandbox' | 'trusted'
  runs_as_asked?: boolean; approval_id?: string | null; mounts?: { instance: string; path: string }[]; problem?: string
}
/** One address of an instance (plan 26 AP08): `url` is what people open, `redirect_uri` what the identity home must hold for the sign-in at that name. The certificate fields are secret references, never a certificate. */
export interface AppAddressRow {
  host: string; kind: 'subdomain' | 'custom'; path: string; primary: boolean
  certificate: string | null; certificate_password: string | null; url?: string | null; redirect_uri?: string | null
  /** A custom domain's DNS challenge: the TXT record that proves the name is the administrator's (plan 26 AP08). */
  challenge?: AppDomainRecord | null
}
/** A DNS record to create. */
export interface AppDomainRecord { name: string; type: string; value: string }
/** What `POST /api/v1/app-instances/{name}/verify-address` answers: accepted by `challenge` (the TXT record holds the value) or `address` (the name resolves to this host), or `reason` why not; `record` is the one to create. */
export interface AppDomainCheck { host: string; verified: boolean; via: 'challenge' | 'address' | null; reason: string | null; record: AppDomainRecord }
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

/** A semantic model the group serves (`GET /api/v1/semantic-models`, plan 33 UE12). `served_version` is what this instance serves now; it lags the group's by a poll at most. */
export interface SemanticModelRow {
  name: string
  current_version: number | null
  versions: number
  managed_by: string | null
  served: boolean
  served_version: number | null
  failed_version: number | null
  failure: string | null
}

export interface ModelVersionRow { version: number; published_by: string | null; managed_by?: string | null; published_at: string | null }

/** One semantic model read in full (`GET /api/v1/semantic-models/{name}`): its sources and every version. */
export interface SemanticModelDetail {
  name: string
  current_version: number
  catalog: string
  sources: Record<string, string>
  ssas: string[]
  versions: ModelVersionRow[]
}

/** An entity model the group serves (`GET /api/v1/entity-models`): a DbDataStore model published to this Gateway. */
export interface EntityModelRow {
  name: string
  current_version: number
  data_source: string
  managed_by: string | null
  served_version: number | null
  failed_version: number | null
  failure: string | null
  entities: string[] | null
}

export interface EntityModelDetail { name: string; current_version: number; data_source: string; managed_by: string | null; versions: ModelVersionRow[] }

/** A secret this gateway keeps for the console (plan 33, UE16): its name and the reference a data source's file writes. Never its value. */
export interface SecretItem { name: string; reference: string }
export interface SecretsList { secrets: string[]; items: SecretItem[] }
export interface SecretWritten { name: string; reference: string; replaced: boolean }

/** server.yaml as the Gateway shows it (plan 33, UE17): secrets masked, the version of the real file, the keys a running Gateway reads at start only. */
export interface ServerSettings { text: string; version: string | null; masked: boolean; protected: string[]; restart_keys: string[] }
export interface SettingsFields { modules?: string[]; 'views.cacheSeconds'?: number; 'views.export.mode'?: string | null; 'views.export.path'?: string | null; logSql?: boolean; 'telemetry.enabled'?: boolean; 'telemetry.endpoint'?: string | null; 'telemetry.protocol'?: string; 'semantic.level'?: number; 'semantic.combine.memoryMb'?: number; 'semantic.combine.timeoutSeconds'?: number; 'semantic.combine.maxInputRows'?: number; 'rest.port'?: number; 'rest.defaultLimit'?: number; 'rest.maxRows'?: number }
export interface SettingsCheck { valid: boolean; diagnostics: Diagnostic[]; fields: SettingsFields; changed: string[]; restart_keys: string[]; problems: string[] }
export interface SettingsEdit { set?: Record<string, string | number | boolean>; remove?: string[]; list?: Record<string, string[]> }
export interface SettingsWritten { version: string; changed: string[]; restart_keys: string[]; restart_needed: boolean; commit: string | null }

/** What a model's catalog says (`GET /api/v1/semantic-models/{name}/catalog`, plan 14a SL12): no data source and no connection. */
export interface CatalogBinding { source: string; join: string | null; column: string; dax: string | null }
export interface CatalogAttribute { name: string; type: string; synonyms: string[]; bindings: CatalogBinding[]; sort_key: string | null; time?: boolean }
export interface CatalogDimension { name: string; label: string | null; default_role: string | null; attributes: CatalogAttribute[] }
export interface CatalogMeasure { name: string; type: string; source: string | null; aggregation: string | null; column: string | null; derived: string | null; format: Record<string, unknown> | null; synonyms: string[]; roles: Record<string, string>; filtered?: boolean }
export interface CatalogQuery { name: string; description: string | null; sql: string | null; request: Record<string, unknown> | null; parameters: Record<string, unknown> }
export interface SemanticCatalogView {
  name: string
  /** The published version this is, null for a model set up in `server.yaml`. */
  version: number | null
  published: boolean
  timezone: string
  dimensions: CatalogDimension[]
  measures: CatalogMeasure[]
  hierarchies: { name: string; levels: string[] }[]
  calendars: { name: string; date: string; implicit?: boolean; levels: Record<string, string> }[]
  segments: string[]
  queries: CatalogQuery[]
  sources: { name: string; kind: string; fact_table: string; joins: string[] }[]
}

/** What a prune answers (plan 30 PB05): the versions removed or, in a dry run, the ones that would be, the candidates kept with the reason, and the files that go with them. */
export interface PruneAnswer { kind: string; name: string; dry_run: boolean; remove: number[]; retained: { version: number; reason: string }[]; files: string[] }
