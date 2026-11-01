import type { LineageGraphDoc } from '@dbdatakit/visuals'
import { describe, expect, it } from 'vitest'
import { findCards, serverNote } from './lineage'

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
