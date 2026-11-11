import type { GraphEdge, GraphNode } from './types'

export interface PlacedNode {
  node: GraphNode
  x: number
  y: number
}

export interface PlacedEdge {
  edge: GraphEdge
  /** The path from the right edge of the source to the left edge of the target. */
  path: string
}

export interface GraphLayout {
  width: number
  height: number
  nodes: PlacedNode[]
  edges: PlacedEdge[]
}

export const NODE_WIDTH = 190
export const NODE_HEIGHT = 40
const COLUMN_GAP = 70
const ROW_GAP = 16
const MARGIN = 16

/**
 * A layered picture of a project: one column per level (sources on the left, what is built from them to the right), nodes in a column in name order, and a curve for each edge.
 * Pure, so it can be tested without a browser.
 */
export function layoutGraph(nodes: readonly GraphNode[], edges: readonly GraphEdge[]): GraphLayout {
  const levels = [...new Set(nodes.map((n) => n.level))].sort((a, b) => a - b)
  const placed = new Map<string, PlacedNode>()
  let tallest = 0
  levels.forEach((level, column) => {
    const inLevel = nodes.filter((n) => n.level === level).sort((a, b) => a.name.localeCompare(b.name))
    tallest = Math.max(tallest, inLevel.length)
    inLevel.forEach((node, row) => {
      placed.set(node.name, { node, x: MARGIN + column * (NODE_WIDTH + COLUMN_GAP), y: MARGIN + row * (NODE_HEIGHT + ROW_GAP) })
    })
  })

  const placedEdges: PlacedEdge[] = []
  for (const edge of edges) {
    const from = placed.get(edge.from)
    const to = placed.get(edge.to)
    if (!from || !to) continue
    const x1 = from.x + NODE_WIDTH
    const y1 = from.y + NODE_HEIGHT / 2
    const x2 = to.x
    const y2 = to.y + NODE_HEIGHT / 2
    const bend = Math.max(24, (x2 - x1) / 2)
    placedEdges.push({ edge, path: `M ${x1} ${y1} C ${x1 + bend} ${y1}, ${x2 - bend} ${y2}, ${x2} ${y2}` })
  }

  return {
    width: MARGIN * 2 + Math.max(1, levels.length) * NODE_WIDTH + Math.max(0, levels.length - 1) * COLUMN_GAP,
    height: MARGIN * 2 + Math.max(1, tallest) * NODE_HEIGHT + Math.max(0, tallest - 1) * ROW_GAP,
    nodes: [...placed.values()],
    edges: placedEdges,
  }
}

/** The names a model depends on, directly or through others, and the names that depend on it: what lights up when one is chosen. */
export function relatives(name: string, edges: readonly GraphEdge[]): { upstream: Set<string>; downstream: Set<string> } {
  const walk = (start: string, next: (e: GraphEdge) => [string, string]) => {
    const seen = new Set<string>()
    const queue = [start]
    while (queue.length > 0) {
      const current = queue.shift()!
      for (const e of edges) {
        const [from, to] = next(e)
        if (from === current && !seen.has(to)) {
          seen.add(to)
          queue.push(to)
        }
      }
    }
    return seen
  }
  return { upstream: walk(name, (e) => [e.to, e.from]), downstream: walk(name, (e) => [e.from, e.to]) }
}
