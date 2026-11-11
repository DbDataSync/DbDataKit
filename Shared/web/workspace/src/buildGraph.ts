import { parseYaml } from '@dbdatakit/shell'
import type { GraphEdge, GraphNode } from '@dbdatakit/build/graph'

/** The graph of one or more connections' compiled loads, and what could not be read. */
export interface LoadGraph {
  nodes: GraphNode[]
  edges: GraphEdge[]
  /** Connections whose `loads.yml` was compiled before the dependency graph was carried (no `dependencies:` key): their models are shown, their edges cannot be. */
  withoutGraph: string[]
  /** Connections whose text is not a loads file, with why. */
  unreadable: { connection: string; why: string }[]
}

const asList = (value: unknown): Record<string, unknown>[] => (Array.isArray(value) ? value.filter((v): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)) : [])
const text = (value: unknown): string | null => (typeof value === 'string' && value !== '' ? value : null)

/** What `loads.yml` of one connection says about the project's models (`dbdatabuild`'s `LoadsDocument`: `models:` with name, kind, file and connections, `dependencies:` with the direct and the transitive edges, `loads:` with each load's strategy); read loosely, the tool is the authority. */
export function readLoads(source: string, connection: string): { models: { name: string; kind: string; file: string | null; connections: string[] }[]; edges: GraphEdge[]; hasGraph: boolean } | { error: string } {
  const parsed = parseYaml(source)
  if (parsed.errors.length > 0) return { error: parsed.errors[0] }
  const top = parsed.value
  if (typeof top !== 'object' || top === null || Array.isArray(top) || !('loads_file' in top)) return { error: 'Not a loads file: it has no `loads_file` header.' }
  const doc = top as Record<string, unknown>
  const byName = new Map<string, { name: string; kind: string; file: string | null; connections: string[] }>()
  const note = (name: string | null, kind: string | null, file: string | null, connections: string[]) => {
    if (!name) return
    const known = byName.get(name)
    if (known) {
      if (known.kind === '' && kind) known.kind = kind
      if (known.file === null) known.file = file
      return
    }
    byName.set(name, { name, kind: kind ?? '', file, connections: connections.length > 0 ? connections : [connection] })
  }
  for (const m of asList(doc.models)) note(text(m.name), text(m.kind), text(m.file), Array.isArray(m.connections) ? m.connections.filter((c): c is string => typeof c === 'string') : [])
  for (const l of asList(doc.loads)) note(text(l.model), text(l.strategy), null, [])
  const hasGraph = Array.isArray(doc.dependencies)
  const edges: GraphEdge[] = []
  for (const d of asList(doc.dependencies)) {
    const model = text(d.model)
    const on = text(d.depends_on)
    note(model, text(d.model_kind), null, [])
    note(on, text(d.depends_on_kind), null, [])
    // depth 1 is a direct read; the longer ones are the shortest path through other things and would draw every edge twice over
    const depth = typeof d.depth === 'number' ? d.depth : 1
    if (model && on && depth <= 1) edges.push({ from: on, to: model })
  }
  return { models: [...byName.values()], edges, hasGraph }
}

/** Every node placed on a level: a model that reads nothing is level 0, one that reads others is one more than the deepest of them. A cycle (which the compiler refuses) stops at the level it was found at instead of looping. */
export function levelsOf(names: readonly string[], edges: readonly GraphEdge[]): Map<string, number> {
  const reads = new Map<string, string[]>()
  for (const e of edges) reads.set(e.to, [...(reads.get(e.to) ?? []), e.from])
  const level = new Map<string, number>()
  const walk = (name: string, trail: Set<string>): number => {
    const known = level.get(name)
    if (known !== undefined) return known
    if (trail.has(name)) return 0
    trail.add(name)
    const deepest = Math.max(-1, ...(reads.get(name) ?? []).map((n) => walk(n, trail)))
    trail.delete(name)
    level.set(name, deepest + 1)
    return deepest + 1
  }
  for (const n of names) walk(n, new Set())
  return level
}

/** The graph of a project from the texts of its connections' `loads.yml` (committed under `rendered/<connection>/`), the connections merged by model name. */
export function graphOfLoads(files: readonly { connection: string; text: string }[]): LoadGraph {
  const models = new Map<string, GraphNode>()
  const edges = new Map<string, GraphEdge>()
  const withoutGraph: string[] = []
  const unreadable: { connection: string; why: string }[] = []
  for (const f of files) {
    const read = readLoads(f.text, f.connection)
    if ('error' in read) { unreadable.push({ connection: f.connection, why: read.error }); continue }
    if (!read.hasGraph) withoutGraph.push(f.connection)
    for (const m of read.models) {
      const known = models.get(m.name)
      if (known) { known.connections = [...new Set([...known.connections, ...m.connections])]; known.kind ||= m.kind; known.file ??= m.file } else {
        models.set(m.name, { name: m.name, category: 'model', kind: m.kind, level: 0, file: m.file, connections: m.connections })
      }
    }
    for (const e of read.edges) edges.set(`${e.from}>${e.to}`, e)
  }
  const placed = levelsOf([...models.keys()], [...edges.values()])
  const nodes = [...models.values()].map((n) => ({ ...n, level: placed.get(n.name) ?? 0 }))
  return { nodes, edges: [...edges.values()], withoutGraph, unreadable }
}
