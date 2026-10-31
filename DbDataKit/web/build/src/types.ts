/** A project a Build server hosts (`GET /api/v1/projects`). */
export interface BuildProject {
  id: string
  name: string
  source: 'path' | 'git'
  branch: string | null
}

/** What a command run on the server answers: its exit code, and the JSON document it printed (or its text when it printed none). */
export interface CommandAnswer<T> {
  exit: number
  document: { ok: boolean; data: T; diagnostics?: Diagnostic[]; errors?: string[]; messages?: string[] } | null
  text: string | null
}

export interface Diagnostic {
  code: string
  severity: 'error' | 'warning' | 'note'
  title: string
  location: { file: string; line: number; column: number }
  found: string
  supported: string
  fix: string
}

export interface GraphNode {
  name: string
  category: string
  kind: string
  level: number
  file: string | null
  connections: string[]
}

export interface GraphEdge {
  from: string
  to: string
}

export interface ProjectGraph {
  nodes: GraphNode[]
  edges: GraphEdge[]
}

export interface CompileSummary {
  counts: { models: number; errors: number; warnings: number; notes: number; by_kind: Record<string, number> }
  out_of_date?: string[]
}

/** One line of `connection status`: an object the models declare and where it stands on the target. */
export interface ObjectState {
  name: string
  state: string
}

export interface ConnectionStatus {
  connection: string
  login?: string
  objects: ObjectState[]
  preview?: { steps: number; risky: number; destructive: number; questions: unknown[]; blocked: unknown[]; skipped: unknown[] }
  plans?: { plan: string; outcome: string; health?: { overdue: boolean; slow: boolean } }[]
  summary?: { structure: string; steps: number; loads: number; risky: number; destructive: number; blocked: number; changed_outside_the_tool: number; policy: string }
}

/** A plan file of the project (`project show plan` with no plan named). */
export interface PlanEntry {
  path: string
  id: string
  connection: string
  steps: number
  risky: number
  destructive: number
  intact: boolean
  problems: string[]
}

export interface PlanStep {
  id: string
  type: string
  object: string
  description: string
  text: string
  risk: string
  reasons: string[]
}

/** One plan read in full (`project show plan <file>`). */
export interface PlanDetail {
  path: string
  intact: boolean
  plan: { id: string; connection: string; git_commit: string; git_dirty: boolean; steps: PlanStep[]; bases: { object: string; state: string }[] }
  risky: number
  destructive: number
  apply_needs: { allow_risky: boolean; allow_destructive: string[] }
}

/** What `connection deploy` answers when it writes a plan, dry-runs one or applies one. */
export interface DeployResult {
  connection: string
  dry_run?: boolean
  plan_id?: string
  success?: boolean
  plan?: { id: string }
  outcomes?: { step?: string; status?: string; description?: string }[]
}

/** `project compile --content`: each file the compiler would write, with its text. */
export interface RenderedFiles {
  files: { path: string; hash: string; content: string }[]
}

export interface SampleTable {
  name: string
  kind: string
  columns: { name: string; type: string }[]
  row_count: number
  rows: (string | null)[][]
  error?: string | null
}

export interface SampleResult {
  rows_per_declared_model: number
  seed: number
  limit: number
  tables: SampleTable[]
}

/** `connection compare`: two tables by counts and digests (values are never read through the console). */
export interface CompareResult {
  left: { table: string; rows: number; columns: number }
  right: { table: string; rows: number; columns: number }
  key: { columns: string[]; source: string }
  schema: {
    compared_columns: string[]
    only_left: string[]
    only_right: string[]
    not_compared: { column: string; left_type: string; right_type: string; reason: string }[]
    type_differences: unknown[]
  }
  duplicate_keys: { left: number; right: number }
  rows: { compared: boolean; only_left: number; only_right: number; matched: number; differing: number; differing_by_column: Record<string, number> }
  identical: boolean
}

/** One person's draft of a project (`GET /api/v1/projects/{p}/workspaces`). */
export interface Workspace {
  id: string
  name: string
  owner: string
  mine: boolean
  branch: string
  base: string
  created: string
}

/** A file of the project tree, with the hash a write must name to be accepted. */
export interface ProjectFile {
  path: string
  size: number
  hash: string
}

export interface FileContent {
  path: string
  hash: string
  content: string
}

/** A file the draft changes from where it started. */
export interface GitChange {
  path: string
  status: string
  patch: string
  /** The whole text at the start (null for a new file) and now (null for a deleted one); only when `sides` is true. */
  before?: string | null
  after?: string | null
  /** False for a binary or very large file, which has its patch only. */
  sides?: boolean
}

export interface GitCommit {
  sha: string
  message: string
  author: string
  when: string
}

/** A proxy this Build server is paired with, as the server lists it. */
export interface PairedProxy {
  name: string
  url: string
  instance_id: string
  paired_at: string
}

/** What publishing a connection to a proxy answered. */
export interface PublishAnswer {
  proxy: string
  data_source: string
  created: boolean
  version: string | null
}

/** What a refused publish carries when a person could settle it by taking the data source over on the proxy (T12): the client id to name and the call to make there. */
export interface AdoptHint {
  client: string
  data_source: string
  call: string
}

/** What the proxy answers when a data source is handed over (T13): `adopted` is false when it already was the client's. */
export interface AdoptAnswer {
  name: string
  managed_by: string
  adopted: boolean
  previous_owner?: string | null
}

/** A project template built into the server (`GET /api/v1/templates`). */
export interface ProjectTemplateInfo {
  name: string
  description: string
  files: number
}

/** The templates, and whether the caller may make a project from one (the author role on the server). */
export interface TemplateCatalog {
  templates: ProjectTemplateInfo[]
  can_create: boolean
}

/** One command of a project as the server lists it, with whether this caller may run it. */
export interface CommandOffer {
  tool: string
  name: string
  role: string | null
  allowed: boolean
}

/** One option of a definition question. A choice that takes a value (a type, a column name) says what to type. */
export interface QuestionOption {
  key: string
  description: string
  consequence?: string | null
  takes_value: boolean
  value_hint?: string | null
}

/** What the tool inferred as the answer, never taken unless the person says so. */
export interface QuestionProposal {
  option: string
  value?: string | null
  certainty: string
  evidence?: string[]
}

/** A question `project model update` asks about a model's definition: a stable id, the words, the choices and perhaps a proposal. */
export interface DefinitionQuestion {
  id: string
  prompt: string
  context: string[]
  options: QuestionOption[]
  proposal?: QuestionProposal | null
}

/** One answer, as the answers file has it: a choice (with its value) or the inferred proposal accepted. */
export type DefinitionAnswer = { id: string; choice: string; value?: string } | { id: string; accept: 'inferred' }

/** What `project model update` says for one model query. */
export interface DefinedModel {
  query: string
  definition: string
  status: string
  notes: string[]
  open_questions: DefinitionQuestion[]
}

export interface DefineResult {
  mode: string
  models: DefinedModel[]
  written?: string[]
}

/** A table or view of a connection, as `project import` shows it: what it would become as a mapped model. */
export interface ImportRow {
  name: string
  object_kind: string
  file: string | null
  /** new (no mapped model yet), changed, unchanged, stale (a model but no table), invalid (a file that does not load). */
  status: string
  columns?: { name: string; native_type: string; logical_type: string | null; nullable: boolean }[]
  changes: { kind: string; column: string | null; detail: string }[]
  notes: string[]
}

export interface ImportResult {
  connection: string
  mode: string
  mapped_models: ImportRow[]
  skipped: { object: string; reason: string }[]
  written?: string[]
}

/** The connections a project names, from its metadata. */
export interface ProjectConnections {
  project: { config: { connections: Record<string, { engine: string }> } }
}
