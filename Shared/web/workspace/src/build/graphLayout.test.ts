import { describe, expect, it } from 'vitest'
import { layoutGraph, relatives, NODE_HEIGHT, NODE_WIDTH } from './graphLayout'
import type { GraphEdge, GraphNode } from './types'

const node = (name: string, level: number): GraphNode => ({ name, category: 'model', kind: 'view', level, file: null, connections: [] })

const nodes = [node('raw.a', 0), node('raw.b', 0), node('stg.a', 1), node('mart.x', 2)]
const edges: GraphEdge[] = [
  { from: 'raw.a', to: 'stg.a' },
  { from: 'raw.b', to: 'stg.a' },
  { from: 'stg.a', to: 'mart.x' },
]

describe('layoutGraph', () => {
  it('puts each level in a column, in name order, and sizes the picture to the tallest column', () => {
    const layout = layoutGraph(nodes, edges)
    const at = (name: string) => layout.nodes.find((n) => n.node.name === name)!
    expect(at('raw.a').x).toBe(at('raw.b').x)
    expect(at('raw.a').y).toBeLessThan(at('raw.b').y)
    expect(at('stg.a').x).toBeGreaterThan(at('raw.a').x)
    expect(at('mart.x').x).toBeGreaterThan(at('stg.a').x)
    expect(layout.height).toBeGreaterThanOrEqual(2 * NODE_HEIGHT)
    expect(layout.width).toBeGreaterThanOrEqual(3 * NODE_WIDTH)
  })

  it('draws an edge from the right of its source to the left of its target, and skips one whose end is not in the project', () => {
    const layout = layoutGraph(nodes, [...edges, { from: 'ghost', to: 'stg.a' }])
    expect(layout.edges).toHaveLength(3)
    const source = layout.nodes.find((n) => n.node.name === 'raw.a')!
    expect(layout.edges[0].path.startsWith(`M ${source.x + NODE_WIDTH} ${source.y + NODE_HEIGHT / 2}`)).toBe(true)
  })

  it('lays out an empty project as an empty picture', () => {
    const layout = layoutGraph([], [])
    expect(layout.nodes).toHaveLength(0)
    expect(layout.width).toBeGreaterThan(0)
  })
})

describe('relatives', () => {
  it('finds what a model is built from and what is built from it, through others', () => {
    const { upstream, downstream } = relatives('stg.a', edges)
    expect([...upstream].sort()).toEqual(['raw.a', 'raw.b'])
    expect([...downstream]).toEqual(['mart.x'])
    expect([...relatives('mart.x', edges).upstream].sort()).toEqual(['raw.a', 'raw.b', 'stg.a'])
  })

  it('does not loop on a cycle', () => {
    const cyclic: GraphEdge[] = [{ from: 'a', to: 'b' }, { from: 'b', to: 'a' }]
    expect([...relatives('a', cyclic).downstream].sort()).toEqual(['a', 'b'])
  })
})
