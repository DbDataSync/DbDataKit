import type { RenderedError } from '../types'
import type { SelectableLayout } from './selectLayout'
import type { ViewFormatValue } from './format'
import type { LinkDoc } from './links'

/**
 * The shapes of a dashboard or report document as the renderers and layout engines read it (plan 32 sections 2 and 3). They describe a document that has already passed `validateViewsDocument`, so
 * they are as loose as the engines need (a chart's encodings are the Vega-Lite subset's channels, a query is whatever form the file holds) and no looser. Nothing here validates.
 */

export type WidgetType = 'metric' | 'chart' | 'matrix' | 'table' | 'text' | 'image' | 'control' | 'embed' | 'custom'
export type ControlKind = 'select' | 'multiselect' | 'date' | 'daterange' | 'number' | 'toggle' | 'text'
export type ParameterValue = string | number | boolean | null | Array<string | number | boolean>
/** Parameter name to its value now: what a `control` edits and what a request is bound with. */
export type ParameterValues = Record<string, ParameterValue>

export interface ParameterDefinition {
  name: string
  type: 'string' | 'integer' | 'number' | 'boolean'
  for?: string
  many?: boolean
  required?: boolean
  default?: ParameterValue
  label?: string
}

interface WidgetCommon { title?: string }
type QueryDoc = Record<string, unknown>
export interface MetricDoc extends WidgetCommon { type: 'metric'; label?: string; format?: ViewFormatValue; encodings: { value: string; compare?: string }; query: QueryDoc }
export interface ChartDoc extends WidgetCommon {
  type: 'chart'
  mark: string | { type: string; [key: string]: unknown }
  encodings: Record<string, string | Record<string, unknown>>
  query: QueryDoc
  on_select?: { set: string; from: string }
  drill?: unknown
  on_click?: string
}
export interface MatrixDoc extends WidgetCommon { type: 'matrix'; encodings: Record<string, unknown>; query: QueryDoc; on_select?: { set: string; from: string }; drill?: unknown; on_click?: string }
export interface TableColumnDoc {
  field: string; label?: string; format?: ViewFormatValue; align?: 'left' | 'right' | 'center'; width?: number
  /** The object form of a measure (plan 32 DBR50): a new name for the output and its own filters. */
  alias?: string; filters?: unknown[]
  /** How the value reads in the cell (DBR51): text, an icon chosen by the value (`icons`, a closed vocabulary), or a badge; `link` names a link of the document a click on the cell follows. */
  display?: 'text' | 'icon' | 'badge'; icons?: Record<string, string>; link?: string
}
export interface TableDoc extends WidgetCommon { type: 'table'; encodings: { columns: TableColumnDoc[]; sort?: unknown }; query: QueryDoc; drill?: unknown; on_click?: string }
export interface TextDoc extends WidgetCommon { type: 'text'; text: string }
export interface ImageDoc extends WidgetCommon { type: 'image'; hash: string; media_type: 'image/png' | 'image/jpeg' | 'image/webp'; alt: string; fit?: 'contain' | 'cover' }
export interface ControlDoc extends WidgetCommon { type: 'control'; parameter: string; control?: ControlKind; label?: string; values?: { attribute: string; limit?: number } }
export interface EmbedDoc extends WidgetCommon { type: 'embed'; url: string; with?: Record<string, string>; link_text?: string }
/** Code of an app (DBR30): drawn only by the app's own host. */
export interface CustomDoc extends WidgetCommon { type: 'custom'; component: string; params?: Record<string, string | number | boolean>; links?: string[] }
export type WidgetDoc = CustomDoc | MetricDoc | ChartDoc | MatrixDoc | TableDoc | TextDoc | ImageDoc | ControlDoc | EmbedDoc

export interface TileDoc { widget: string; x: number; y: number; w: number; h: number; min?: { w?: number; h?: number }; layer?: number }
export interface WidgetOptionsDoc { density?: 'compact' | 'comfortable'; show_title?: boolean; legend?: 'show' | 'hide' }
interface LayoutCommon extends SelectableLayout { widget_options?: Record<string, WidgetOptionsDoc> }
export interface ReflowLayoutDoc extends LayoutCommon { type: 'reflow'; grid: { columns: number; rows?: number }; tiles: TileDoc[] }
export interface CanvasLayoutDoc extends LayoutCommon {
  type: 'canvas'
  canvas: { width: number; height: number; unit?: 'px' | 'mm' | 'in' }
  overflow?: 'scale' | 'scroll'
  min_scale?: number
  max_scale?: number
  tiles: TileDoc[]
}
export interface FlowLayoutDoc extends LayoutCommon { type: 'flow'; page?: Record<string, unknown>; header?: Record<string, string>; footer?: Record<string, string>; cover?: unknown; sections: unknown[] }
export type LayoutDoc = ReflowLayoutDoc | CanvasLayoutDoc | FlowLayoutDoc

/** The parts of a flow layout as the editor reads them (`FlowLayoutDoc.sections` stays loose for the engines): a section holds blocks; a block is a widget, a row of spans or a forced page break. */
export interface FlowCellDoc { widget: string; span: number }
export type FlowBlockDoc =
  | { widget: string; height?: number; keep?: boolean; repeat_header?: boolean }
  | { row: FlowCellDoc[]; keep?: boolean }
  | { pagebreak: true }
export interface FlowSectionDoc { title?: string; break?: 'before' | 'after'; blocks: FlowBlockDoc[] }

export interface ViewsDoc {
  schemaVersion: number
  kind: 'dashboard' | 'report'
  title: string
  parameters?: ParameterDefinition[]
  /** The language numbers and dates are written in (DBR46); `en` when absent. */
  locale?: 'en' | 'de' | 'fr' | 'es' | 'it' | 'pt' | 'nl'
  /** The links the document declares by name (DBR35); a widget follows one with `on_click`. */
  links?: Record<string, LinkDoc>
  widgets: Record<string, WidgetDoc>
  layouts: LayoutDoc[]
  refresh?: { every: string }
  /** The layout `?mode=kiosk` shows (a wall layout, usually); `?layout=` still wins. */
  kiosk?: { layout: string }
  [key: string]: unknown
}

/** A box in CSS pixels. */
export interface Box { width: number; height: number }

export type { RenderedError }
