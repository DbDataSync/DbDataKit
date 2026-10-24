// Types of the calls the admin pages make; the shapes the server answers with.

// Was a closed union of the three built-ins; now a plain string so a driver installed at runtime
// (a YAML descriptor or a compiled plugin) is just another value of it — see `GET /api/drivers`
// (added in phase 109d), which is what the connection editor's picker will read from instead of a
// hardcoded list.
export type DriverType = string

/** What kind of value a parameter takes, and therefore what control it gets. A closed set: the point
 * of declaring a parameter is that this app can render it without knowing what it is for. */
export type ParameterType =
  | 'Text' | 'Number' | 'Bool' | 'Date' | 'DateTime' | 'Dropdown' | 'ColumnPicker' | 'Property' | 'Secret'
  /** A SQL statement, which `ParameterForm` renders as a real editor rather than a one-line input. */
  | 'Sql'

export interface ParameterCardinality {
  min: number
  max: number
}

/** Hints, not layout. A declaration that says nothing still renders. */
export interface ParameterLayout {
  card: string
  group: string
  /** Relative width within its group, in flex units. */
  size: number
}

/** One setting an author declared and an operator fills in — a driver's, a handler's, or a
 * script's. The server says what exists and this app renders it generically, the same relationship
 * DriverCapabilities already has with the Kind pickers. */
export interface ParameterDescriptor {
  name: string
  label: string | null
  description: string | null
  type: ParameterType
  required: boolean
  /** Null — the common case — means a single value. */
  cardinality: ParameterCardinality | null
  dropdownOptions: string[] | null
  /** How to spell each option for a person, keyed by the stored value. Null where the value already
   * reads as itself, which is most of them. */
  dropdownLabels: Record<string, string> | null
  /** What the form pre-fills. Null means empty, which is distinct from an empty string. */
  default: string | null
  layout: ParameterLayout | null

  /** Whether this parameter applies at all, given the other values. Decided by whoever declared it —
   * a driver knows that Host is beside the point in connection-string mode — so this app renders
   * what it is told rather than holding a second copy of the rule that could disagree. */
  visible: boolean

  /** Whether changing this value changes what the thing takes, and so whether the form has to ask
   * again. Declared, so a form refetches on the dropdown that matters and not on every keystroke. */
  recalc: boolean
}

/** Kind-name-only view of a driver's capabilities for a catalogue listing (phase 118, the admin
 * Drivers screen) — the full per-Kind parameter detail lives on `DriverCapabilities` instead, which is
 * connection-scoped. */
export interface DriverCapabilitySummary {
  readers: string[]
  staging: string[]
  writers: string[]
}

/** `GET /api/known-driver-kinds` — the driver-authoring form's own capability checkboxes source these
 * rather than hardcoding a list, since `GenericDriverBase`'s own construction is the real source of
 * truth for which Kind strings are valid. */
export interface DriverKindsSummary {
  readers: string[]
  staging: string[]
  writers: string[]
}

/** The raw `driver.yaml` text — `GET`/`POST`/`PUT /api/drivers(/{id}/yaml)`, the driver-authoring
 * form's own load/create/save. Raw, not a structured re-derivation, so a hand-authored field the
 * form's structured controls don't model round-trips unchanged. */
export interface DriverYaml {
  yaml: string
}

/** Phase 182N — `GET /api/drivers/{id}/status`. `error` is present exactly when `registered` is false. */
export interface DriverStatusResult {
  registered: boolean
  error: string | null
}

/** Phase 181N — `POST /api/drivers/validate`'s own echo: what a `driver.yaml` actually resolves to,
 * built straight from the parsed descriptor (never a live `IDriver`/spec). `interpreted` is present
 * whenever the yaml at least parsed, even if the later build step failed — "here's what I understood
 * before I hit a problem" survives a build failure, not just a clean parse. */
export interface DriverValidationResult {
  valid: boolean
  error: string | null
  interpreted: DriverInterpretationPreview | null
}

export interface DriverInterpretationPreview {
  id: string
  displayName: string
  base: 'adonet' | 'jdbc'
  dialect: DialectPreview
  typeMap: Record<string, string>
  readers: string[]
  staging: string[]
  writers: string[]
  jdbc: JdbcPreview | null
  testQuery: string | null
}

export interface DialectPreview {
  quoteIdentifier: string
  parameterPrefix: string
  rowLimit: string
  catalog: string
  defaultDatabase: string
  defaultPort: number | null
  connectionStringKeys: ConnectionStringKeysPreview
}

export interface ConnectionStringKeysPreview {
  host: string
  port: string | null
  database: string
  username: string
  password: string
  connectTimeout: string | null
  integratedSecurity: string | null
}

export interface JdbcPreview {
  driverClass: string
  driverJarPaths: string[]
  urlTemplate: string | null
  connectionStringKeys: ConnectionStringKeysPreview
}

/** One entry from `GET /api/drivers` — every driver currently registered: built-in, added from a
 * `driver.yaml` descriptor (phase 109d), or a compiled plugin (phase 109e). */
export interface DriverSummary {
  id: DriverType
  displayName: string
  builtIn: boolean
  source: 'builtin' | 'descriptor' | 'compiled'
  /** The bound `DbDataSync.Libraries` id for a descriptor driver; null for a built-in or a compiled
   * plugin (phase 118). */
  library: string | null
  capabilities: DriverCapabilitySummary
}

/** One installed library — `GET /api/libraries` (phase 118). */
export interface LibrarySummary {
  id: string
  packages: { id: string; version: string }[]
  factoryType: string
  /** A real `DbProviderFactory` resolution probe, not just "the manifest parsed". */
  resolves: boolean
  /** Every descriptor driver on disk whose `library:` names this id. */
  usedBy: string[]
  /** Whether this id matches a bundled `KnownLibraries` entry. */
  curated: boolean
  /** Phase 121: true when this library was written with no SDK available to restore it (the
   * runtime-only image) and no in-image catalog cache hit for it — `library sync` on a host
   * with the SDK finishes it. Distinct from `resolves` being false, which also covers a library whose
   * files are just broken. */
  pendingRestore: boolean
}

/** One file in the `files/` store — `GET /api/files` (phase 173V). A standard place for anything an
 * operator uploads for a driver to reference (a JDBC jar, so far the only real case). */
export interface FileSummary {
  name: string
  sizeBytes: number
  uploadedAt: string
  /** Every JDBC-backed descriptor driver on disk whose `jdbc.driverJarPaths` names this file. */
  usedBy: string[]
}

/** One file's own upload outcome — `POST /api/files` reports one per file in the request, not
 * all-or-nothing over a single stale name. */
export interface UploadResult {
  name: string
  succeeded: boolean
  error: string | null
}

/** One bundled, vetted library — `GET /api/known-libraries` (phase 117/118), the "available to add"
 * list for the Libraries screen. */
export interface KnownLibrarySummary {
  id: string
  displayName: string
  description: string
  packageId: string
  pinnedVersion: string
}

/** One bundled, ready-made driver descriptor — `GET /api/known-drivers` (phase 117/118), the
 * "available to add" list for the Drivers screen. */
export interface KnownDriverSummary {
  id: string
  displayName: string
  description: string
  boundLibrary: string
}

/** One row from `GET /api/libraries/search` (phase 119) — a NuGet package, reshaped from the public
 * search index. `id` is what a caller would pass as `packageId` to `library install`. */
export interface LibrarySearchResult {
  id: string
  description: string
  latestVersion: string
  versions: string[]
  totalDownloads: number
  verified: boolean
}

/** `"ok"` (`results` may still be empty), `"disabled"` (`DbDataSync:Nuget:Search:Mode` is disabled — the
 * server never called out), or `"unavailable"` (the call was made but failed or timed out). The SPA
 * falls back to manual package-id/version entry for anything other than `"ok"`. */
export type LibrarySearchStatus = 'ok' | 'disabled' | 'unavailable'

export interface LibrarySearchResponse {
  status: LibrarySearchStatus
  results: LibrarySearchResult[] | null
}

/** `POST /api/libraries` (phase 120) response — the manifest `LibraryInstaller` wrote. */
export interface LibraryManifest {
  id: string
  factoryType: string
  packages: { id: string; version: string }[]
}

/** `POST /api/drivers/from-catalog` (phase 120) response. */
export interface FromCatalogResult {
  id: string
  library: string
}

/** `GET /api/admin/restart-required` (phase 120) — whether this process has a config value, library,
 * or driver change on disk it hasn't picked up yet. */
export interface RestartRequiredStatus {
  required: boolean
}

/**
 * One row of the admin config screen (phase 81) — a DbDataSync:* key docs/configuration.md documents, its live
 * effective value and where that came from, and what this screen can do about it.
 *
 * `value` is null both for a genuinely unset key and — StateConnectionString only — when `masked` is
 * true: the server never sends a raw credential it found in a non-file source, so the two cases are
 * indistinguishable from `value` alone and the SPA must check `masked` to tell them apart.
 */
export interface AdminConfigEntry {
  key: string
  /** The configured value — what `source` says right now, and what `editable` lets you change.
   * Independent of whether the running process has picked it up yet; see `runningValue`. */
  value: string | null
  /** What this process actually loaded at startup and is running with right now, frozen at boot.
   * Differs from `value` exactly when a change is queued and not yet applied — a save made here, or
   * an override that just moved this key from its environment/CLI/default source into the file. */
  runningValue: string | null
  source: 'file' | 'environment variable' | 'command line' | 'appsettings.json' | 'default'
  editable: boolean
  canAdopt: boolean
  /** True for a file-sourced key with a known application default that `value` doesn't already equal —
   * the inverse of `canAdopt`: putting the factory value back rather than taking a non-file one out. */
  canReset: boolean
  /** What Reset would write, or null when this key's default is contextual (a machine-derived path)
   * rather than a fixed literal. `canReset` is never true when this is null. */
  defaultValue: string | null
  masked: boolean
  description: string
  /** What a numeric value is counted in ("days", "minutes", "runs") — null for a key that isn't a
   * plain magnitude. Shown as a pill only beside a value that's actually numeric. */
  unit: string | null
  /** A plain-language warning to show beside the key in both states — for a setting whose "on" widens what a mistake or an
   * attacker can reach (phase 161). Null for every other key. */
  caution: string | null
  /** The complete, closed set of legal values (lowercase) for a mode-string setting backed by a real
   * enum, e.g. `["disabled", "manual"]` — render a dropdown/toggle instead of a free-text box. Null for
   * a setting with no fixed set, or an open-ended one (State:Engine — a custom dialect can be registered
   * beyond the three built-ins). */
  allowedValues: string[] | null
  /** The provider that outranks dbdatasync.config.yaml for this key — "environment variable" or
   * "command line" — or null, the normal case. `source` still reads "file" whenever the file carries
   * the key, since that is what this screen edits; this is whether editing it will change anything.
   * Without it a save that can never take effect looks exactly like one that did. */
  overriddenBy: string | null
  /** What `overriddenBy` says. Null whenever `overriddenBy` is. */
  overriddenValue: string | null
}

export interface CurrentCertificateInfo {
  thumbprint: string
  subjectCommonName: string
  dnsNames: string[]
  issuer: string
  notBefore: string
  notAfter: string
  daysRemaining: number
  selfSigned: boolean
}

/** `subject` is null only when nothing has ever been bound. `certificateFound` is false when a bound
 * subject no longer matches anything in the store — the same case `dbdatasync cert status` reports as an
 * error, shown here rather than thrown. */
export interface BindingInfo {
  subject: string | null
  store: string
  location: string
  allowInvalid: boolean
  certificateFound: boolean
}

/**
 * Three states, because "unknown" is real: on a host with no DbDataSync Windows service installed (or no
 * certificate bound yet to check), the question does not apply, and this deliberately does not fall back
 * to a green "ok" the way the CLI's own convenience default would — see
 * AdminCertificateService.EvaluateKeyAccess's own doc comment.
 */
export type KeyAccessState = 'Ok' | 'Warning' | 'Unknown'

/** `account` is null only for the `Unknown` case caused by no service being installed at all — every
 * other state, including the other `Unknown` case (no certificate to check), names a real account. */
export interface KeyAccessInfo {
  state: KeyAccessState
  account: string | null
  detail: string | null
}

export interface PendingEnrollmentSummary {
  requestId: string
  subjectCommonName: string
  dnsNames: string[]
  submittedAtUtc: string
}

/** Why `templates` came back empty — `Available` is the one case it did not (and even then the CA may
 * legitimately publish zero templates). */
export type TemplateListReason =
  | 'Available' | 'CaConfigNotSet' | 'NotDomainJoined' | 'DirectoryUnreachable' | 'AccessDenied' | 'Unknown'

/** Backs the Template field's picker. When `reason` is not `Available`, the field renders as free text
 * showing `detail` instead — never disabled, and enrollment never blocked on this having worked. */
export interface TemplateListResult {
  templates: string[]
  reason: TemplateListReason
  detail: string | null
}

export interface CertificateCandidate {
  thumbprint: string
  subjectCommonName: string
  dnsNames: string[]
  notAfter: string
  daysRemaining: number
  selfSigned: boolean
}

/** `available` is false on a non-Windows host, in which case every other field but
 * `unavailableReason` is null/empty — the SPA renders one explanatory line rather than an empty
 * section. */
export interface AdminCertificateStatus {
  available: boolean
  unavailableReason: string | null
  certificate: CurrentCertificateInfo | null
  binding: BindingInfo | null
  keyAccess: KeyAccessInfo | null
  pendingEnrollments: PendingEnrollmentSummary[]
  templates: TemplateListResult | null
  /** The same threshold the daily expiry check raises a notification at — "days remaining" is coloured
   * against this number, not a second one the SPA invented. */
  expiryWarningDays: number
}

/**
 * `succeeded` is false only for a genuine failure — the API sends that case as a 400 with `{ error }`,
 * which `ErrorBanner` already renders, so a mutation's `onError` is what a page actually reads for a
 * failure. This type is what a *successful* response carries: `requestId` is set only when an
 * enrollment came back pending (a template requiring approval, not a failure), and `status` is the
 * refreshed AdminCertificateStatus so the page does not need a second round trip.
 */
export interface CertificateActionResult {
  succeeded: boolean
  message: string
  requestId: string | null
  status: AdminCertificateStatus | null
}

// Updating this installation from the console — phase 159. Phases are the server's, lower-cased: an update is
// idle, or moves through staging (a snapshot downloading), draining (waiting for running work), applying (the
// service is about to restart), and restarting (the new version is installed and proving itself), and ends
// succeeded, rolledback or failed.
export type UpdatePhase =
  | 'idle' | 'staging' | 'draining' | 'applying' | 'restarting' | 'succeeded' | 'rolledback' | 'failed'

export type UpdateChannel = 'stable' | 'beta' | 'snapshot'

export interface UpdateHistoryEntry {
  phase: UpdatePhase
  message: string | null
  fromVersion: string | null
  toVersion: string | null
  atUtc: string
  requestedBy: string | null
}

/** The commands that update this installation from a shell on the server (phase 196L) — the console itself never
 * applies an update. Built by the server, which knows its OS and data directory. */
export interface UpdateCommands {
  /** Where to run them, and with what privilege, in a sentence. */
  where: string
  list: string
  status: string
  /** Has `versionPlaceholder` where the chosen version goes. */
  apply: string
  /** Null when there is no earlier version in the other slot to switch back to. */
  rollback: string | null
  versionPlaceholder: string
  /** A machine-wide install from before versioned slots: the first apply converts it, once, and says so. */
  convertsFirst: boolean
  /** A global tool: `apply` only prints the `dotnet tool` commands to run. */
  printsOnly: boolean
}

export interface UpdateSlot {
  name: 'a' | 'b'
  /** Null when the slot is empty. */
  version: string | null
  current: boolean
  ambiguous: boolean
}

export interface UpdateSlots {
  /** The tool directory: the launcher, `current.txt` and `versions/`. */
  root: string
  current: 'a' | 'b' | null
  slots: UpdateSlot[]
  /** The pointer checked against what is on disk: `warning`s stop the launcher starting; `note`s are worth knowing. */
  checks: { level: 'note' | 'warning'; message: string }[]
}

export interface UpdateStatus {
  /** `DbDataSync:Updates:Mode`: whether releases are looked up. Disabled by default; the commands are given either way. */
  enabled: boolean
  runningVersion: string | null
  /** `ToolPath`, `Global`, `Container` or `NotAToolInstall`. */
  installKind: string
  /** The channels the operator has allowed the console to offer. */
  channels: UpdateChannel[]
  /** Null for a container or a copy that is not a tool install; `commandsUnavailableReason` says why. */
  commands: UpdateCommands | null
  commandsUnavailableReason: string | null
  /** Null when this process was not started by the launcher (an install from before versioned slots). */
  slots: UpdateSlots | null
  /** What the CLI last recorded — an update run from a shell shows here while it runs. */
  phase: UpdatePhase
  message: string | null
  atUtc: string | null
  fromVersion: string | null
  toVersion: string | null
  requestedBy: string | null
  logPath: string
  history: UpdateHistoryEntry[]
}

export interface UpdateRelease {
  version: string
  channel: UpdateChannel
  builtUtc: string | null
  installed: boolean
  newer: boolean
  /** A page a human can read about this exact version — the GitHub Release page. */
  url: string
}

export interface UpdateReleases {
  releases: UpdateRelease[]
  /** A channel that could not be read while another could — never fatal. */
  warnings: string[]
}
