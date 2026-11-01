import type { LineageProjection } from './types'

export interface Reach {
  /** What feeds the node, however far back (the provenance). */
  upstream: ReadonlySet<string>
  /** What the node feeds, however far on (the impact). */
  downstream: ReadonlySet<string>
}

/** The cards a card is fed by and the cards it feeds, along the lines of the projection. A line is followed only in its own direction, so a card that merely shares a source is in neither. */
export function reach(projection: LineageProjection, id: string): Reach {
  const feeds = new Map<string, string[]>()
  const fedBy = new Map<string, string[]>()
  for (const link of projection.links) {
    ;(feeds.get(link.from) ?? feeds.set(link.from, []).get(link.from)!).push(link.to)
    ;(fedBy.get(link.to) ?? fedBy.set(link.to, []).get(link.to)!).push(link.from)
  }
  const walk = (start: string, next: Map<string, string[]>) => {
    const seen = new Set<string>()
    const queue = [start]
    while (queue.length > 0) {
      for (const n of next.get(queue.pop()!) ?? []) if (n !== start && !seen.has(n)) { seen.add(n); queue.push(n) }
    }
    return seen
  }
  return { upstream: walk(id, fedBy), downstream: walk(id, feeds) }
}
