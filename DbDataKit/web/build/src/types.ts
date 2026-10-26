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

export interface SampleTable {
  name: string
  kind: string
  columns: { name: string; type: string }[]
  row_count: number
  rows: (string | null)[][]
}

export interface SampleResult {
  rows_per_declared_model: number
  seed: number
  limit: number
  tables: SampleTable[]
}
