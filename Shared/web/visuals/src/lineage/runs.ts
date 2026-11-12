import type { LineageGraphDoc, LineageLink } from './types'

/** What happened along an edge when it last ran (plan 18 G10b): the console sends the newest run of each edge a server knows (`runs` of `GET /api/lineage`). */
export interface LineageRunDoc {
  from: string
  to: string
  kind: string
  at: string
  outcome: 'succeeded' | 'failed'
  rows: number | null
  /** The server says the edge is overdue by its own expectation of how often it runs (`HealthRule.Overdue`); absent when it is not. */
  late?: boolean
}

/** The runs a drawn line stands for: it is `failed` when any edge folded into it failed, else `succeeded`; `at` is the newest; `late` when any edge folded into it is overdue. */
export interface LinkRun {
  outcome: 'succeeded' | 'failed'
  at: string
  rows: number | null
  failed: number
  total: number
  late?: boolean
}

/** The state a line is drawn in: `failed` is red, `late` amber (its last run succeeded but it is overdue), `fresh` green. */
export function runState(run: LinkRun): 'failed' | 'late' | 'fresh' {
  return run.outcome === 'failed' ? 'failed' : run.late ? 'late' : 'fresh'
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
    result.set(link.id, { outcome: failed > 0 ? 'failed' : 'succeeded', at: newest.at, rows: known.length === 0 ? null : known.reduce((n, r) => n + (r.rows ?? 0), 0), failed, total: mine.length, late: mine.some((r) => r.late === true) || undefined })
  }
  return result
}

/** The words on a line for its run: `replicates · failed 2026-11-10 08:00`, `replicates · late since 2026-11-10 08:00` or `derives · ok`; the line's own name when it has no run. */
export function runLabel(kind: string, run: LinkRun | undefined): string {
  if (!run) return kind
  const when = run.at.slice(0, 16).replace('T', ' ')
  const state = runState(run)
  return state === 'failed' ? `${kind} · failed ${when}` : state === 'late' ? `${kind} · late, last ok ${when}` : `${kind} · ok ${when}`
}
