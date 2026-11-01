import { describe, expect, it } from 'vitest'
import { layoutLineage } from './layout'
import { projectLineage } from './project'
import { reach } from './reach'
import type { LineageGraphDoc } from './types'

const crm = 'sqlserver://sql01/crm.dbo.customers'
const warehouse = 'postgres://pg01/dwh.public.customers'
const model = 'postgres://pg01/dwh.marts.dim_customer'
const measure = 'suite://build-1/semantic/retail_sales/customers.region'

/** Sync writes the warehouse table from the CRM, Build derives a mart from it, a semantic attribute is bound from a column of the mart. */
const graph: LineageGraphDoc = {
  nodes: [
    { id: crm, kind: 'dataset', name: 'dbo.customers', unknown_upstream: true, properties: { connection: 'crm' } },
    { id: `${crm}#customerid`, kind: 'column', name: 'customerid', unknown_upstream: true },
    { id: warehouse, kind: 'dataset', name: 'public.customers', unknown_upstream: false },
    { id: `${warehouse}#customer_id`, kind: 'column', name: 'customer_id', unknown_upstream: false },
    { id: `${warehouse}#region`, kind: 'column', name: 'region', unknown_upstream: false },
    { id: model, kind: 'dataset', name: 'marts.dim_customer', unknown_upstream: false },
    { id: `${model}#region`, kind: 'column', name: 'region', unknown_upstream: false },
    { id: measure, kind: 'semanticmember', name: 'retail_sales: customers.region', unknown_upstream: false, properties: { catalog: 'retail_sales' } },
  ],
  edges: [
    { from: crm, to: warehouse, kind: 'replicates', granularity: 'table', confidence: 'declared', detail: 'crm-to-dwh/customers' },
    { from: `${crm}#customerid`, to: `${warehouse}#customer_id`, kind: 'replicates', granularity: 'column', confidence: 'declared' },
    { from: warehouse, to: model, kind: 'derives', granularity: 'table', confidence: 'derived' },
    { from: `${warehouse}#region`, to: `${model}#region`, kind: 'derives', granularity: 'column', confidence: 'derived', detail: 'identity' },
    { from: `${model}#region`, to: measure, kind: 'binds', granularity: 'column', confidence: 'declared' },
  ],
}

describe('projectLineage', () => {
  it('draws a card for every node that is not a column and folds the column edges into the lines between cards', () => {
    const { cards, links } = projectLineage(graph)

    expect(cards.map((c) => c.id).sort()).toEqual([crm, measure, model, warehouse].sort())
    expect(cards.every((c) => c.columns.length === 0 && !c.expanded)).toBe(true)
    expect(cards.find((c) => c.id === warehouse)!.columnCount).toBe(2)          // there is something to open, though none is listed
    expect(cards.find((c) => c.id === measure)!.columnCount).toBe(0)
    // the table edge and the column edge between the CRM and the warehouse are one line, folded
    const sync = links.find((l) => l.from === crm && l.to === warehouse)!
    expect(sync).toMatchObject({ kind: 'replicates', folded: 2, detail: 'crm-to-dwh/customers' })
    expect(links.find((l) => l.from === model && l.to === measure)).toMatchObject({ kind: 'binds', folded: 1 })
    expect(links).toHaveLength(3)
  })

  it('lists the columns of an expanded dataset and draws the column edges to its rows, leaving the other cards whole', () => {
    const { cards, links } = projectLineage(graph, new Set([model]))

    const open = cards.find((c) => c.id === model)!
    expect(open.expanded).toBe(true)
    expect(open.columns.map((c) => c.name)).toEqual(['region'])
    expect(open.height).toBeGreaterThan(cards.find((c) => c.id === warehouse)!.height)
    expect(cards.find((c) => c.id === warehouse)!.columns).toEqual([])
    expect(links.find((l) => l.from === model && l.to === measure)).toMatchObject({ fromColumn: `${model}#region` })
    expect(links.find((l) => l.from === warehouse && l.to === model && l.toColumn)).toMatchObject({ toColumn: `${model}#region`, kind: 'derives' })
  })

  it('has no card for a column whose dataset is not in the graph, and no line for an edge to a node it does not have', () => {
    const { cards, links } = projectLineage({
      nodes: [{ id: 'a#x', kind: 'column', name: 'x', unknown_upstream: false }],
      edges: [{ from: 'a#x', to: 'b', kind: 'derives', granularity: 'column', confidence: 'derived' }],
    })

    expect(links).toEqual([])
    expect(cards.map((c) => c.id)).toEqual(['a#x'])
  })
})

describe('layoutLineage', () => {
  it('places every line pointing right and no card over another, the same way each time', () => {
    for (const expanded of [new Set<string>(), new Set([model, warehouse])]) {
      const projection = projectLineage(graph, expanded)
      const placed = layoutLineage(projection)
      const at = new Map(placed.map((p) => [p.id, p]))

      for (const link of projection.links) {
        const [from, to] = [at.get(link.from)!, at.get(link.to)!]
        expect(from.x + from.width).toBeLessThanOrEqual(to.x)
      }
      for (const a of placed)
        for (const b of placed)
          if (a !== b) expect(a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height).toBe(false)
      expect(layoutLineage(projection)).toEqual(placed)
    }
  })

  it('lays out a graph of five hundred cards', () => {
    const nodes = Array.from({ length: 500 }, (_, i) => ({ id: `n${i}`, kind: 'dataset', name: `t${i}`, unknown_upstream: false }))
    const edges = nodes.flatMap((n, i) => (i % 63 === 62 ? [] : [{ from: n.id, to: `n${Math.min(499, i + 63)}`, kind: 'derives', granularity: 'table' as const, confidence: 'derived' }]))

    const placed = layoutLineage(projectLineage({ nodes, edges }))

    expect(placed).toHaveLength(500)
    expect(new Set(placed.map((p) => `${p.x},${p.y}`)).size).toBe(500)
  })
})

describe('reach', () => {
  it('finds what feeds a card and what it feeds, each along its own direction, however far', () => {
    const projection = projectLineage(graph)

    expect(reach(projection, warehouse)).toEqual({ upstream: new Set([crm]), downstream: new Set([model, measure]) })
    expect(reach(projection, crm).upstream.size).toBe(0)
    expect(reach(projection, measure)).toEqual({ upstream: new Set([model, warehouse, crm]), downstream: new Set() })
  })

  it('does not count a card that only shares a source', () => {
    const sibling = 'postgres://pg01/dwh.public.other'
    const projection = projectLineage({
      nodes: [...graph.nodes, { id: sibling, kind: 'dataset', name: 'public.other', unknown_upstream: false }],
      edges: [...graph.edges, { from: crm, to: sibling, kind: 'replicates', granularity: 'table', confidence: 'declared' }],
    })

    expect(reach(projection, sibling).upstream).toEqual(new Set([crm]))
    expect(reach(projection, warehouse).downstream.has(sibling)).toBe(false)
    expect(reach(projection, warehouse).upstream.has(sibling)).toBe(false)
  })
})
