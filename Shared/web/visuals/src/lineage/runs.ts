import type { LineageGraphDoc, LineageLink } from './types'

/** What happened along an edge when it last ran (plan 18 G10b): the console sends the newest run of each edge a server knows (`runs` of `GET /api/lineage`). */
export interface LineageRunDoc {
  from: string
  to: string
  kind: string
  at: string
  outcome: 'succeeded' | 'failed'
  rows: number | null
}

/** The runs a drawn line stands for: it is `failed` when any edge folded into it failed, else `succeeded`; `at` is the newest. */
export interface LinkRun {
  outcome: 'succeeded' | 'failed'
  at: string
  rows: number | null
  failed: number
  total: number
}

/**
 * Which runs belong to which line. A line joins two cards (a table-level line folds the column edges between them) or two column rows (when a card is expanded). A run names an edge by its own ends, which may be columns, so each end is
 * taken to its card first; a line at column level takes only the runs of exactly those columns. Keyed by the line's id.
 */
export function runsOfLinks(graph: LineageGraphDoc, links: readonly LineageLink[], runs: readonly LineageRunDoc[] | undefined): Map<string, LinkRun> {
  const result = new Map<string, LinkRun>()
  if (!runs || runs.length === 0) return result
  const byId = new Map(graph.nodes.map((n) => [n.id, n]))
  const cardOf = (id: string): string => {
    const node = byId.get(id)
    if (node?.kind !== 'column') return id
    const at = id.lastIndexOf('#')
    return at < 0 || !byId.has(id.slice(0, at)) ? id : id.slice(0, at)
  }
  for (const link of links) {
    const mine = runs.filter((r) =>
      r.kind === link.kind && cardOf(r.from) === link.from && cardOf(r.to) === link.to &&
      (link.fromColumn === undefined || r.from === link.fromColumn) && (link.toColumn === undefined || r.to === link.toColumn))
    if (mine.length === 0) continue
    const failed = mine.filter((r) => r.outcome === 'failed').length
    const newest = mine.reduce((a, b) => (Date.parse(b.at) > Date.parse(a.at) ? b : a))
    const known = mine.filter((r) => r.rows !== null)
    result.set(link.id, { outcome: failed > 0 ? 'failed' : 'succeeded', at: newest.at, rows: known.length === 0 ? null : known.reduce((n, r) => n + (r.rows ?? 0), 0), failed, total: mine.length })
  }
  return result
}

/** The words on a line for its run: `replicates · failed 2026-11-10 08:00` or `derives · ok`; the line's own name when it has no run. */
export function runLabel(kind: string, run: LinkRun | undefined): string {
  if (!run) return kind
  const when = run.at.slice(0, 16).replace('T', ' ')
  return run.outcome === 'failed' ? `${kind} · failed ${when}` : `${kind} · ok ${when}`
}
