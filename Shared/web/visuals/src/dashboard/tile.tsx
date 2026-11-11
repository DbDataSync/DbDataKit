import type { Theme } from '../theme'
import type { Box, LayoutDoc, ParameterDefinition, ParameterValue, ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { WidgetView } from '../widgets/WidgetView'
import type { Hit, HitHow } from './interaction'

/** What every layout engine's frame needs to draw one widget: shared by `DashboardFrame` and `CanvasFrame`, so a tile is the same in both. */
export interface TileContext {
  document: Pick<ViewsDoc, 'widgets' | 'parameters'>
  layout: LayoutDoc
  theme: Theme
  results: Record<string, RenderedWidget>
  loading?: ReadonlySet<string>
  parameters?: ParameterValues
  onParameterChange?: (parameter: string, value: ParameterValue) => void
  onMaximize?: (widget: string | null) => void
  /** A widget was pointed at (cross-filter, drill): which widget, what, how. */
  onHit?: (widget: string, hit: Hit, how: HitHow) => void
  /** When each widget's rows were read (hh:mm) and whether the last refresh failed; given only where the document refreshes. */
  asOf?: Record<string, string>
  stale?: boolean
  /** The host follows a `drill` (default yes). */
  canDrill?: boolean
  /** Viewers sort a table or a matrix by clicking a header (plan 32 DBR45): on screen only, never stored; the print and export routes do not set it. */
  sortable?: boolean
  /** A table the server cut: the host runs it again with `order` (widget, column field, direction; null, null for the document's order). */
  onServerSort?: (widget: string, field: string | null, dir: 'asc' | 'desc' | null) => void
}

export function tileView(c: TileContext, id: string, box: Box, isMaximized: boolean) {
  const widget = c.document.widgets[id]
  if (!widget) return null
  return (
    <WidgetView
      id={id}
      widget={widget}
      result={c.results[id]}
      loading={c.loading?.has(id) ?? c.results[id] === undefined}
      box={box}
      theme={c.theme}
      options={c.layout.widget_options?.[id]}
      parameters={c.parameters}
      definitions={c.document.parameters as ParameterDefinition[] | undefined}
      onParameterChange={c.onParameterChange}
      maximized={isMaximized}
      onMaximize={c.onMaximize ? () => c.onMaximize!(isMaximized ? null : id) : undefined}
      onHit={c.onHit ? (hit, how) => c.onHit!(id, hit, how) : undefined}
      asOf={c.asOf?.[id]}
      stale={c.stale}
      canDrill={c.canDrill}
      sortable={c.sortable}
      onServerSort={c.onServerSort}
    />
  )
}
