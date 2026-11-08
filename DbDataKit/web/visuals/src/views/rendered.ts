import type { DataValue, RenderedChart, RenderedMatrix } from '../types'

/**
 * The rendered widgets (plan 32 section 2 item 5, DBR04's `rendered-widgets`): what the console's `ViewsClient` gives for each widget a layout places, and what the widget renderers draw. A matrix and a chart are
 * the shapes of `rendered-view.schema.json` unchanged; the others are new. **Provisional:** DBR04 writes the output schema and this file follows it; until then the recorded results of the sample bundle
 * (`conformance/views/widget-results`) are the examples. Nothing here is computed in the browser: a number reads as the server formatted it (`text`), a comparison arrives with its change.
 */

export interface RenderedNumber { v?: number | null; text: string }

export interface RenderedMetric {
  type: 'metric'
  value: RenderedNumber | null
  compare?: (RenderedNumber & { change?: { text: string; direction: 'up' | 'down' | 'flat' } }) | null
}

export interface RenderedTable {
  type: 'table'
  columns: Array<{ field: string; label: string; align?: 'left' | 'right' | 'center' }>
  /** A total row is a row of the result and says so. */
  rows: Array<{ total?: boolean; cells: Array<{ v?: DataValue; text: string }> }>
  /** The rows the query had when the server cut them at its limit. */
  total_rows?: number
}

/** A text widget has no query: it is drawn from the document (with the parameters substituted as text). */
export interface RenderedText { type: 'text' }

/** An image widget's bytes as the client resolved them from the definition's artifacts: a `data:` URI of a raster. A remote address is never drawn. */
export interface RenderedImage { type: 'image'; src: string }

/** What a control's list holds (a select's values under the viewer's access); a date, number or text control has none. */
export interface RenderedControl { type: 'control'; values?: Array<{ value: string | number | boolean; label?: string }> }

/** One widget that failed (denied, timed out, refused by the engine, no recorded result): it draws as an error tile, the rest of the page draws. */
export interface RenderedWidgetError { type: 'error'; code: string; message: string; path?: string | null; suggestions?: string[] | null }

export type RenderedWidget = RenderedMetric | RenderedTable | RenderedMatrix | RenderedChart | RenderedText | RenderedImage | RenderedControl | RenderedWidgetError

/** What one resolution of a layout gives: the widgets it placed (those asked for, when `only` narrowed it), by id. */
export interface RenderedWidgets {
  kind: 'rendered_widgets'
  layout: string
  /** When the rows were read (ISO 8601); a fixture says so too. */
  as_of: string
  widgets: Record<string, RenderedWidget>
}
