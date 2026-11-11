import { useState } from 'react'
import { layoutGraph, NODE_HEIGHT, NODE_WIDTH, relatives } from '../graphLayout'
import type { GraphEdge, GraphNode } from '../types'

export type { GraphEdge, GraphNode }

/**
 * The project's models as a layered picture. Choosing one lights up what it is built from and what is built from it. It draws a graph it is given and asks for nothing, so the Build screens (the graph the Build server answers)
 * and the Workspace (the graph read from the draft's committed plan files, plan 33 SC20h) show the same picture.
 */
export function GraphView({ nodes, edges }: { nodes: readonly GraphNode[]; edges: readonly GraphEdge[] }) {
  const [chosen, setChosen] = useState<string | null>(null)
  const layout = layoutGraph(nodes, edges)
  const related = chosen ? relatives(chosen, edges) : null
  const dim = (name: string) => related !== null && name !== chosen && !related.upstream.has(name) && !related.downstream.has(name)
  return (
    <div className="card" style={{ overflow: 'auto' }} data-testid="build-graph">
      <svg width={layout.width} height={layout.height} role="img" aria-label="The project's models and what each is built from">
        {layout.edges.map(({ edge, path }) => {
          const lit = related !== null && (edge.from === chosen || edge.to === chosen || (related.upstream.has(edge.from) && (related.upstream.has(edge.to) || edge.to === chosen)) || (related.downstream.has(edge.to) && (related.downstream.has(edge.from) || edge.from === chosen)))
          return <path key={`${edge.from}>${edge.to}`} d={path} fill="none" stroke="currentColor" strokeWidth={lit ? 2 : 1} opacity={related === null ? 0.45 : lit ? 0.9 : 0.12} data-testid="build-graph-edge" />
        })}
        {layout.nodes.map(({ node, x, y }) => (
          <g key={node.name} transform={`translate(${x} ${y})`} opacity={dim(node.name) ? 0.25 : 1} style={{ cursor: 'pointer' }} onClick={() => setChosen(chosen === node.name ? null : node.name)} data-testid={`build-graph-node-${node.name}`}>
            <rect width={NODE_WIDTH} height={NODE_HEIGHT} rx={5} fill="var(--surface, #fff)" stroke="currentColor" strokeWidth={node.name === chosen ? 2.5 : 1} />
            <text x={10} y={17} fontSize={12} fontFamily="var(--mono, monospace)" fill="currentColor">{node.name}</text>
            <text x={10} y={32} fontSize={10.5} fill="currentColor" opacity={0.65}>{node.kind}</text>
          </g>
        ))}
      </svg>
    </div>
  )
}
