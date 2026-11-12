import { describe, expect, it } from 'vitest'
import { projectLineage } from './project'
import { runLabel, runsOfLinks, runState, type LineageRunDoc } from './runs'
import type { LineageGraphDoc } from './types'

const crm = 'sqlserver://sql01/crm.dbo.customers'
const warehouse = 'postgres://pg01/dwh.public.customers'
const model = 'postgres://pg01/dwh.marts.dim_customer'

const graph: LineageGraphDoc = {
  nodes: [
    { id: crm, kind: 'dataset', name: 'dbo.customers', unknown_upstream: false },
    { id: `${crm}#id`, kind: 'column', name: 'id', unknown_upstream: false },
    { id: warehouse, kind: 'dataset', name: 'public.customers', unknown_upstream: false },
    { id: `${warehouse}#id`, kind: 'column', name: 'id', unknown_upstream: false },
    { id: model, kind: 'dataset', name: 'marts.dim_customer', unknown_upstream: false },
  ],
  edges: [
    { from: crm, to: warehouse, kind: 'replicates', granularity: 'table', confidence: 'declared' },
    { from: `${crm}#id`, to: `${warehouse}#id`, kind: 'replicates', granularity: 'column', confidence: 'declared' },
    { from: warehouse, to: model, kind: 'derives', granularity: 'table', confidence: 'derived' },
  ],
}

const run = (from: string, to: string, kind: string, outcome: LineageRunDoc['outcome'], at: string, rows: number | null = null): LineageRunDoc => ({ from, to, kind, at, outcome, rows })

describe('runsOfLinks (plan 18 G10b)', () => {
  it('marks the line of a failed edge, takes a column edge to its cards and leaves a line with no run alone', () => {
    const links = projectLineage(graph).links
    const result = runsOfLinks(graph, links, [
      run(`${crm}#id`, `${warehouse}#id`, 'replicates', 'failed', '2026-11-10T08:00:00+00:00', 10),
      run(crm, warehouse, 'replicates', 'succeeded', '2026-11-10T07:00:00+00:00', 5),
      run(warehouse, model, 'derives', 'succeeded', '2026-11-10T09:00:00+00:00', 7),
    ])
    const replicates = result.get(links.find((l) => l.kind === 'replicates')!.id)!
    expect(replicates).toMatchObject({ outcome: 'failed', failed: 1, total: 2, rows: 15, at: '2026-11-10T08:00:00+00:00' })
    expect(result.get(links.find((l) => l.kind === 'derives')!.id)!.outcome).toBe('succeeded')
  })

  it('draws a column line from the runs of exactly those columns', () => {
    const links = projectLineage(graph, new Set([crm, warehouse])).links
    const column = links.find((l) => l.fromColumn)!
    const result = runsOfLinks(graph, links, [run(crm, warehouse, 'replicates', 'failed', '2026-11-10T08:00:00+00:00'), run(`${crm}#id`, `${warehouse}#id`, 'replicates', 'succeeded', '2026-11-10T08:00:00+00:00')])
    expect(result.get(column.id)!.outcome).toBe('succeeded')
  })

  it('is empty without runs and words a line for its run', () => {
    expect(runsOfLinks(graph, projectLineage(graph).links, undefined).size).toBe(0)
    expect(runLabel('derives', undefined)).toBe('derives')
    expect(runLabel('replicates', { outcome: 'failed', at: '2026-11-10T08:00:00+00:00', rows: null, failed: 1, total: 1 })).toBe('replicates · failed 2026-11-10 08:00')
    expect(runLabel('derives', { outcome: 'succeeded', at: '2026-11-10T09:00:00+00:00', rows: 7, failed: 0, total: 1 })).toBe('derives · ok 2026-11-10 09:00')
  })
})

describe('late and fresh (plan 18 G10b)', () => {
  it('is late when a run folded into the line is overdue, failed beats late and a run that is not late is fresh', () => {
    const links = projectLineage(graph).links
    const replicates = links.find((l) => l.kind === 'replicates')!
    const late = runsOfLinks(graph, links, [{ ...run(crm, warehouse, 'replicates', 'succeeded', '2026-11-10T08:00:00+00:00'), late: true }]).get(replicates.id)!
    expect(late).toMatchObject({ outcome: 'succeeded', late: true })
    expect(runState(late)).toBe('late')
    expect(runLabel('replicates', late)).toBe('replicates · late, last ok 2026-11-10 08:00')
    expect(runState({ ...late, outcome: 'failed' })).toBe('failed')
    const fresh = runsOfLinks(graph, links, [run(crm, warehouse, 'replicates', 'succeeded', '2026-11-10T08:00:00+00:00')]).get(replicates.id)!
    expect(runState(fresh)).toBe('fresh')
  })
})
