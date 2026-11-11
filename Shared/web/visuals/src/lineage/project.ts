import type { LineageCard, LineageEdgeDoc, LineageGraphDoc, LineageLink, LineageNodeDoc, LineageProjection } from './types'

export const CARD_WIDTH = 240
export const HEADER_HEIGHT = 48
export const COLUMN_HEIGHT = 22

/** The dataset a column node belongs to, from its id (`<dataset>#<column>`); null for a node that is not a column. */
function datasetOf(node: LineageNodeDoc): string | null {
  if (node.kind !== 'column') return null
  const at = node.id.lastIndexOf('#')
  return at < 0 ? null : node.id.slice(0, at)
}

/**
 * What to draw for a lineage graph, at a level of detail. At the **table level** (nothing expanded) every node that is not a column is a card, and the edges between columns are folded into one line between
 * their cards. A dataset in `expanded` lists its columns as rows of its card, and the column edges that touch them are drawn to those rows; every other card stays one card whatever the rest shows.
 */
export function projectLineage(graph: LineageGraphDoc, expanded: ReadonlySet<string> = new Set()): LineageProjection {
  const byId = new Map(graph.nodes.map((n) => [n.id, n]))
  const columnsOf = new Map<string, LineageNodeDoc[]>()
  for (const node of graph.nodes) {
    const dataset = datasetOf(node)
    if (dataset && byId.has(dataset)) (columnsOf.get(dataset) ?? columnsOf.set(dataset, []).get(dataset)!).push(node)
  }

  const cards: LineageCard[] = graph.nodes
    .filter((n) => datasetOf(n) === null || !byId.has(datasetOf(n)!))
    .map((n) => {
      const open = expanded.has(n.id) && (columnsOf.get(n.id)?.length ?? 0) > 0
      const columns = open ? [...columnsOf.get(n.id)!].sort((a, b) => a.name.localeCompare(b.name)).map((c) => ({ id: c.id, name: c.name, unknownUpstream: c.unknown_upstream })) : []
      return {
        id: n.id, kind: n.kind, title: n.name, subtitle: n.properties?.connection ?? n.properties?.catalog, unknownUpstream: n.unknown_upstream,
        columns, columnCount: columnsOf.get(n.id)?.length ?? 0, expanded: open, width: CARD_WIDTH, height: HEADER_HEIGHT + columns.length * COLUMN_HEIGHT,
      }
    })
    .sort((a, b) => a.id.localeCompare(b.id))
  const cardIds = new Set(cards.map((c) => c.id))
  const open = new Set(cards.filter((c) => c.expanded).map((c) => c.id))

  // where an edge end lands: its card, and the column row when that card is expanded and the end is one of its columns
  const endOf = (id: string): { card: string; column?: string } | null => {
    const node = byId.get(id)
    if (!node) return null
    const dataset = datasetOf(node)
    if (dataset && cardIds.has(dataset)) return { card: dataset, column: open.has(dataset) ? id : undefined }
    return cardIds.has(id) ? { card: id } : null
  }

  const links = new Map<string, LineageLink>()
  for (const edge of graph.edges as LineageEdgeDoc[]) {
    const from = endOf(edge.from)
    const to = endOf(edge.to)
    if (!from || !to || from.card === to.card) continue
    const key = [from.card, from.column ?? '', to.card, to.column ?? '', edge.kind].join('|')
    const known = links.get(key)
    if (known) known.folded += 1
    else links.set(key, { id: key, from: from.card, to: to.card, fromColumn: from.column, toColumn: to.column, kind: edge.kind, detail: edge.detail, folded: 1 })
  }
  return { cards, links: [...links.values()].sort((a, b) => a.id.localeCompare(b.id)) }
}
