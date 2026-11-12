import type { AttributeCellParts } from './CellContent'

/** A cell of a rendered table or matrix: the server's text, the number it stands for, and (an attribute's cell, plan 32 DBR51) the icon, badge, mixed marker, values and link it carries. */
export type RenderedMatrixCell = { v?: number | null; text: string } & AttributeCellParts

/** The shapes of a rendered view (`rendered-view.schema.json`), as `ViewRenderer.Render` in DbDataKit.Semantic gives them. */
export interface RenderedMatrix {
  id: string
  type: 'matrix'
  notice?: string | null
  columns: Array<{ labels: string[]; total?: boolean; other?: boolean }>
  rows: Array<{ labels: string[]; total?: boolean; cells: RenderedMatrixCell[] }>
}

export type DataValue = string | number | boolean | null

export interface RenderedChart {
  id: string
  type: 'chart'
  /** A Vega-Lite spec inside the subset, with its data inline as `data.values`. */
  vega_lite: { data: { values: Array<Record<string, DataValue>> } } & Record<string, unknown>
  /** d3-format strings for the quantitative channels (a widget's chart `format`, plan 32 DBR41); a tooltip's is a list in the order of its entries. See `applyChartFormats`. */
  /** The renderer drew a multi-level `x` as an inner `x` and the outer level as `column`: draw the column as group headers below the labels (DBR42). See `applyNestedAxis`. */
  nested_axis?: boolean
  formats?: Partial<Record<'x' | 'y' | 'color' | 'column' | 'row', string>> & { tooltip?: Array<string | null> }
}

export interface RenderedError {
  spec?: string
  code: string
  path?: string | null
  message: string
  suggestions?: string[] | null
}

export interface RenderedViewDocument {
  kind: 'rendered_view'
  status: 'complete' | 'failed'
  views?: Array<RenderedMatrix | RenderedChart> | null
  errors?: RenderedError[] | null
}
