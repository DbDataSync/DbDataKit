import dagre from '@dagrejs/dagre'
import type { LineageProjection, PlacedCard } from './types'

/**
 * Places the cards left to right: a line always points right and no card overlaps another. dagre (17 KB) is enough for the layers a lineage graph has; the comparison with ELK and Cytoscape is
 * `docs/lineage-graph-view.md`. Deterministic: the same projection gives the same places.
 */
export function layoutLineage(projection: LineageProjection): PlacedCard[] {
  const g = new dagre.graphlib.Graph()
  g.setGraph({ rankdir: 'LR', ranksep: 90, nodesep: 24, marginx: 16, marginy: 16 })
  g.setDefaultEdgeLabel(() => ({}))
  for (const card of projection.cards) g.setNode(card.id, { width: card.width, height: card.height })
  const seen = new Set<string>()
  for (const link of projection.links) {
    const key = `${link.from}>${link.to}`
    if (seen.has(key)) continue
    seen.add(key)
    g.setEdge(link.from, link.to)
  }
  dagre.layout(g)
  return projection.cards.map((card) => {
    const place = g.node(card.id)
    return { ...card, x: Math.round(place.x - card.width / 2), y: Math.round(place.y - card.height / 2) }
  })
}
