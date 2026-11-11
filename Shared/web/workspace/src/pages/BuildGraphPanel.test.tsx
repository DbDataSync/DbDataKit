import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { graphOfLoads, levelsOf, readLoads } from '../buildGraph'
import { LoadGraphView } from './BuildGraphPanel'

/** The shape `LoadsDocument.Serialize` writes (`DbDataBuild.Planning/RefreshPlan.cs`): a header, the loads, the dependencies among the loaded models, the models. */
const loads = (connection: string, body: string) => `loads_file:\n  hash: "abc"\n  connection: "${connection}"\n  project_hash: "p"\n  loads: 3\n${body}`
const withGraph = loads('wh', [
  'loads:',
  '  - hash: "h1"', '    model: "stg_orders"', '    load: "default"', '    strategy: "full"', '    file: "wh/stg_orders.sql"',
  '  - hash: "h2"', '    model: "orders"', '    load: "default"', '    strategy: "incremental_by_unique_key"', '    file: "wh/orders.sql"',
  '  - hash: "h3"', '    model: "revenue"', '    load: "default"', '    strategy: "full"', '    file: "wh/revenue.sql"',
  'dependencies:',
  '  - model: "orders"', '    model_kind: "incremental_by_unique_key"', '    depends_on: "stg_orders"', '    depends_on_kind: "full"', '    depth: 1',
  '  - model: "revenue"', '    model_kind: "full"', '    depends_on: "orders"', '    depends_on_kind: "incremental_by_unique_key"', '    depth: 1',
  '  - model: "revenue"', '    model_kind: "full"', '    depends_on: "stg_orders"', '    depends_on_kind: "full"', '    depth: 2',
  'models:',
  '  - name: "stg_orders"', '    kind: "full"', '    file: "models/stg/stg_orders.yml"', '    connections:', '      - "wh"',
  '  - name: "orders"', '    kind: "incremental_by_unique_key"', '    file: "models/marts/orders.yml"',
  '  - name: "revenue"', '    kind: "full"', '    file: "models/marts/revenue.yml"',
  '',
].join('\n'))

describe('the graph read from a compiled loads file', () => {
  it('has the direct edges only, from what is read to what reads it, and a level per model', () => {
    const graph = graphOfLoads([{ connection: 'wh', text: withGraph }])
    expect(graph.edges).toEqual([{ from: 'stg_orders', to: 'orders' }, { from: 'orders', to: 'revenue' }])
    expect(Object.fromEntries(graph.nodes.map((n) => [n.name, n.level]))).toEqual({ stg_orders: 0, orders: 1, revenue: 2 })
    expect(graph.nodes.find((n) => n.name === 'orders')).toMatchObject({ kind: 'incremental_by_unique_key', file: 'models/marts/orders.yml', connections: ['wh'] })
    expect(graph.withoutGraph).toEqual([])
    expect(graph.unreadable).toEqual([])
  })

  it('shows the models of a file compiled before the graph was carried, and says its edges cannot be drawn', () => {
    const old = loads('wh', ['loads:', '  - hash: "h1"', '    model: "a"', '    load: "default"', '    strategy: "full"', '    file: "wh/a.sql"', ''].join('\n'))
    const graph = graphOfLoads([{ connection: 'wh', text: old }])
    expect(graph.nodes.map((n) => [n.name, n.kind])).toEqual([['a', 'full']])
    expect(graph.edges).toEqual([])
    expect(graph.withoutGraph).toEqual(['wh'])
  })

  it('merges two connections by model name and keeps both connections on the model', () => {
    const second = loads('lake', ['dependencies:', '  - model: "orders"', '    model_kind: "full"', '    depends_on: "raw"', '    depends_on_kind: "full"', '    depth: 1', 'models:', '  - name: "orders"', '    kind: "full"', '    file: "models/marts/orders.yml"', '  - name: "raw"', '    kind: "full"', '    file: "models/raw.yml"', ''].join('\n'))
    const graph = graphOfLoads([{ connection: 'wh', text: withGraph }, { connection: 'lake', text: second }])
    expect(graph.nodes.map((n) => n.name).sort()).toEqual(['orders', 'raw', 'revenue', 'stg_orders'])
    expect(graph.nodes.find((n) => n.name === 'orders')!.connections.sort()).toEqual(['lake', 'wh'])
    expect(graph.edges).toHaveLength(3)
  })

  it('reports a text that is not a loads file or not YAML, naming the connection, and draws the others', () => {
    const graph = graphOfLoads([{ connection: 'bad', text: 'refresh_plan:\n  hash: x\n' }, { connection: 'worse', text: 'a: [1' }, { connection: 'wh', text: withGraph }])
    expect(graph.unreadable.map((u) => u.connection)).toEqual(['bad', 'worse'])
    expect(graph.unreadable[0]!.why).toMatch(/loads_file/)
    expect(graph.nodes).toHaveLength(3)
    expect(readLoads('', 'x')).toMatchObject({ error: expect.any(String) })
  })

  it('does not loop on a cycle', () => {
    const levels = levelsOf(['a', 'b'], [{ from: 'a', to: 'b' }, { from: 'b', to: 'a' }])
    expect(levels.size).toBe(2)
  })
})

describe('the load graph panel', () => {
  it('draws Build\'s own picture: a node per model and an edge per direct dependency', () => {
    const html = renderToStaticMarkup(<LoadGraphView graph={graphOfLoads([{ connection: 'wh', text: withGraph }])} connections={1} />)
    expect(html).toContain('data-testid="build-graph-node-stg_orders"')
    expect(html).toContain('data-testid="build-graph-node-revenue"')
    expect(html.match(/data-testid="build-graph-edge"/g)).toHaveLength(2)
    expect(html).toContain('3 models, 2 edges')
  })

  it('says nothing is compiled when the draft has no plan', () => {
    const html = renderToStaticMarkup(<LoadGraphView graph={graphOfLoads([])} connections={0} />)
    expect(html).toContain('wsp-build-graph-none')
    expect(html).not.toContain('build-graph-node')
  })

  it('says a file is old, and names an unreadable one', () => {
    const html = renderToStaticMarkup(<LoadGraphView graph={{ nodes: [], edges: [], withoutGraph: ['wh'], unreadable: [{ connection: 'x', why: 'line 1: bad' }] }} connections={2} />)
    expect(html).toContain('rendered/wh/loads.yml was compiled before')
    expect(html).toContain('wsp-build-graph-unreadable')
  })
})
