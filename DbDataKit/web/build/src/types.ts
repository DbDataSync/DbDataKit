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
