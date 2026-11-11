import type { LineageGraphDoc } from '@dbdatakit/visuals'
import { describe, expect, it } from 'vitest'
import { checkLine, checkStatusOf, findCards, serverNote, withoutDeployments } from './lineage'

describe('withoutDeployments', () => {
  it('drops the deployment nodes and the edges that touch them, and keeps the flow of data', () => {
    const mixed: LineageGraphDoc = {
      nodes: [
        { id: 't1', kind: 'dataset', name: 't1', unknown_upstream: false },
        { id: 't2', kind: 'dataset', name: 't2', unknown_upstream: false },
        { id: 'pkg', kind: 'deployment', name: 'orders', unknown_upstream: false },
        { id: 'run', kind: 'job', name: 'orders/prod/load', unknown_upstream: false, properties: { deployment: 'run' } },
      ],
      edges: [
        { from: 't1', to: 't2', kind: 'derives', granularity: 'table', confidence: 'derived' },
        { from: 'pkg', to: 'run', kind: 'runs', granularity: 'table', confidence: 'observed' },
      ],
    }
    const result = withoutDeployments(mixed)
    expect(result.nodes.map((n) => n.id)).toEqual(['t1', 't2'])
    expect(result.edges).toHaveLength(1)
  })
})

const graph: LineageGraphDoc = {
  nodes: [
    { id: 'postgres://pg01/dwh.public.customers', kind: 'dataset', name: 'public.customers', unknown_upstream: false },
    { id: 'postgres://pg01/dwh.public.customers#region', kind: 'column', name: 'region', unknown_upstream: false },
    { id: 'postgres://pg01/dwh.marts.dim_customer', kind: 'dataset', name: 'marts.dim_customer', unknown_upstream: false },
    { id: 'suite://build-1/semantic/retail_sales/customers.region', kind: 'semanticmember', name: 'retail_sales: customers.region', unknown_upstream: false },
  ],
  edges: [],
}

describe('findCards', () => {
  it('finds a card by any word of its name or id, case ignored, never a column', () => {
    expect(findCards(graph, 'CUSTOMER').map((n) => n.name)).toEqual(['public.customers', 'marts.dim_customer', 'retail_sales: customers.region'])
    expect(findCards(graph, 'region')).toHaveLength(1)                         // the column is not a card
    expect(findCards(graph, 'dwh marts').map((n) => n.name)).toEqual(['marts.dim_customer'])      // every word, in the id too
    expect(findCards(graph, '   ')).toEqual([])
    expect(findCards(graph, 'nothing')).toEqual([])
  })

  it('stops at the limit', () => {
    expect(findCards(graph, 'customer', 2)).toHaveLength(2)
  })
})

describe('serverNote', () => {
  it('says how big a graph is and how much was left out, or why there is none', () => {
    expect(serverNote({ nodes: 12, edges: 9, error: null, problems: [] })).toBe('12 nodes, 9 edges')
    expect(serverNote({ nodes: 12, edges: 9, error: null, problems: ['x', 'y'] })).toBe('12 nodes, 9 edges; 2 left out')
    expect(serverNote({ nodes: null, edges: null, error: 'It did not answer.', problems: [] })).toBe('did not give its lineage: It did not answer.')
  })
})

describe('checks on a node (plan 23a FQ16)', () => {
  const node = (status: string | undefined, extra: Record<string, string> = {}) =>
    ({ id: 'n', kind: 'dataset', name: 'n', unknown_upstream: false, properties: status ? { check_status: status, check_count: '2', check_failing: '1', ...extra } : undefined }) as LineageGraphDoc['nodes'][number]

  it('reads the status only when it is one the server can send', () => {
    expect(checkStatusOf(node('blocked'))).toBe('blocked')
    expect(checkStatusOf(node(undefined))).toBeNull()
    expect(checkStatusOf(node('whatever'))).toBeNull()
  })

  it('says what the checks say and, for a block, what it stops', () => {
    expect(checkLine(node(undefined), [])).toBeNull()
    expect(checkLine(node('pass'), [])).toBe('2 checks, all passing')
    expect(checkLine(node('error'), [])).toBe('2 checks, 1 could not run')
    expect(checkLine(node('fail'), [])).toContain('warn only')
    const blocks = [{ node: 'n', name: 'n', checks: ['fresh'], since: null, stops: [{ id: 'm', name: 'marts.dim', kind: 'dataset', depth: 1 }, { id: 's', name: 'dim served', kind: 'serving', depth: 2 }] }]
    expect(checkLine(node('blocked', { check_blocking_ids: 'fresh,unique' }), blocks)).toBe('2 checks, 1 failing, blocked by fresh, unique; stops 2: marts.dim, dim served')
    expect(checkLine(node('blocked', { check_blocking_ids: 'fresh' }), [])).toContain('nothing depends on it')
  })
})
