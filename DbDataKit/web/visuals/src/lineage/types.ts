/** A joined lineage graph as the console sends it (`GET /api/lineage`, `LineageJson` on the .NET side): kinds in lower case, a column node's id is its dataset's id, `#` and the column. */
export interface LineageNodeDoc {
  id: string
  kind: string
  name: string
  unknown_upstream: boolean
  properties?: Record<string, string>
}

export interface LineageEdgeDoc {
  from: string
  to: string
  kind: string
  granularity: 'table' | 'column'
  confidence: string
  detail?: string
}

export interface LineageGraphDoc {
  nodes: LineageNodeDoc[]
  edges: LineageEdgeDoc[]
}

export interface CardColumn {
  id: string
  name: string
  unknownUpstream: boolean
}

/** What is drawn for a node: a card. Its columns are listed only when the card is expanded. */
export interface LineageCard {
  id: string
  kind: string
  title: string
  subtitle?: string
  unknownUpstream: boolean
  columns: CardColumn[]
  /** How many columns the dataset has in the graph, whether or not they are listed; a card with none has nothing to expand. */
  columnCount: number
  expanded: boolean
  width: number
  height: number
}

/** A line between two cards; `fromColumn` and `toColumn` name a column when that end's card is expanded. `folded` counts the edges it stands for. */
export interface LineageLink {
  id: string
  from: string
  to: string
  fromColumn?: string
  toColumn?: string
  kind: string
  detail?: string
  folded: number
}

export interface LineageProjection {
  cards: LineageCard[]
  links: LineageLink[]
}

export interface PlacedCard extends LineageCard {
  x: number
  y: number
}
