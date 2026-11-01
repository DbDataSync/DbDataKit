import { useMemo, useState } from 'react'
import { Background, Controls, Handle, MarkerType, Position, ReactFlow, type Edge, type Node, type NodeProps } from '@xyflow/react'
import { layoutLineage } from './layout'
import { COLUMN_HEIGHT, HEADER_HEIGHT, projectLineage } from './project'
import type { LineageCard, LineageGraphDoc, PlacedCard } from './types'

interface CardData extends Record<string, unknown> {
  card: LineageCard
  onToggle: (id: string) => void
}

function CardNode({ data }: NodeProps<Node<CardData>>) {
  const { card, onToggle } = data
  return (
    <div className={`lineage-card lineage-card-${card.kind}`} data-testid={`lineage-card-${card.id}`} style={{ width: card.width, height: card.height }}>
      <Handle id="in" type="target" position={Position.Left} style={{ top: HEADER_HEIGHT / 2 }} />
      <Handle id="out" type="source" position={Position.Right} style={{ top: HEADER_HEIGHT / 2 }} />
      <div className="lineage-card-header" style={{ height: HEADER_HEIGHT }}>
        <span className="lineage-card-kind">{card.kind}</span>
        <span className="lineage-card-title" title={card.id}>{card.title}</span>
        {card.unknownUpstream && <span className="lineage-card-unknown" title="What feeds this is not known">?</span>}
        {card.columnCount > 0 && (
          <button type="button" className="lineage-card-toggle" aria-expanded={card.expanded} onClick={() => onToggle(card.id)} data-testid={`lineage-toggle-${card.id}`}>
            {card.expanded ? 'Hide columns' : `${card.columnCount} columns`}
          </button>
        )}
        {card.subtitle && <span className="lineage-card-subtitle">{card.subtitle}</span>}
      </div>
      {card.columns.map((column, i) => (
        <div key={column.id} className="lineage-card-column" style={{ height: COLUMN_HEIGHT }}>
          <Handle id={`in:${column.id}`} type="target" position={Position.Left} style={{ top: HEADER_HEIGHT + i * COLUMN_HEIGHT + COLUMN_HEIGHT / 2 }} />
          <Handle id={`out:${column.id}`} type="source" position={Position.Right} style={{ top: HEADER_HEIGHT + i * COLUMN_HEIGHT + COLUMN_HEIGHT / 2 }} />
          <span>{column.name}</span>
          {column.unknownUpstream && <span className="lineage-card-unknown" title="What feeds this is not known">?</span>}
        </div>
      ))}
    </div>
  )
}

const nodeTypes = { card: CardNode }

export interface LineageGraphViewProps {
  graph: LineageGraphDoc
  /** The datasets whose columns are listed to begin with; the person opens and closes the others. */
  initiallyExpanded?: readonly string[]
  height?: number | string
}

/**
 * The lineage graph (plan 19 Y04, plan 18 G10): the joined graph of the servers as cards left to right, at the table level, a dataset opening to its columns. Laid out by dagre; drawn by React Flow, which renders
 * only the cards in view. The import of `@xyflow/react/dist/style.css` is in `styles.css`.
 */
export function LineageGraphView({ graph, initiallyExpanded = [], height = 600 }: LineageGraphViewProps) {
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set(initiallyExpanded))
  const toggle = (id: string) => setExpanded((now) => {
    const next = new Set(now)
    if (!next.delete(id)) next.add(id)
    return next
  })

  const { nodes, edges } = useMemo(() => {
    const projection = projectLineage(graph, expanded)
    const placed: PlacedCard[] = layoutLineage(projection)
    const nodes: Node<CardData>[] = placed.map((card) => ({ id: card.id, type: 'card', position: { x: card.x, y: card.y }, data: { card, onToggle: toggle }, draggable: false }))
    const edges: Edge[] = projection.links.map((link) => ({
      id: link.id, source: link.from, target: link.to,
      sourceHandle: link.fromColumn ? `out:${link.fromColumn}` : 'out', targetHandle: link.toColumn ? `in:${link.toColumn}` : 'in',
      markerEnd: { type: MarkerType.ArrowClosed }, className: `lineage-link lineage-link-${link.kind}`,
      label: link.folded > 1 ? `${link.kind} (${link.folded})` : link.kind, data: { detail: link.detail },
    }))
    return { nodes, edges }
  }, [graph, expanded])

  return (
    <div className="lineage-graph" style={{ height }} data-testid="lineage-graph">
      <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} fitView onlyRenderVisibleElements nodesConnectable={false} proOptions={{ hideAttribution: true }}>
        <Background />
        <Controls showInteractive={false} />
      </ReactFlow>
    </div>
  )
}
