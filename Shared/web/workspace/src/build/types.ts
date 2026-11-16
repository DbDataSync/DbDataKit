// The shapes the Workspace's Build editors share (plan 33 SC23: moved here from the Build server's screens, which were deleted with the server).

/** What a command run for a draft answers: its exit code, and the JSON document it printed (or its text when it printed none). */
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

/** A file of the project tree, with the hash a write must name to be accepted. */
export interface ProjectFile {
  path: string
  size: number
  hash: string
}
