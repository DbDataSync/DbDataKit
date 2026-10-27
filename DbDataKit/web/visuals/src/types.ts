/** The shapes of a rendered view (`rendered-view.schema.json`), as `ViewRenderer.Render` in DbDataKit.Semantic gives them. */
export interface RenderedMatrix {
  id: string
  type: 'matrix'
  notice?: string | null
  columns: Array<{ labels: string[]; total?: boolean; other?: boolean }>
  rows: Array<{ labels: string[]; total?: boolean; cells: Array<{ v?: number | null; text: string }> }>
}

export type DataValue = string | number | boolean | null

export interface RenderedChart {
  id: string
  type: 'chart'
  /** A Vega-Lite spec inside the subset, with its data inline as `data.values`. */
  vega_lite: { data: { values: Array<Record<string, DataValue>> } } & Record<string, unknown>
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
