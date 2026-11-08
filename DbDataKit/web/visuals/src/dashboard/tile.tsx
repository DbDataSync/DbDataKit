import type { Theme } from '../theme'
import type { Box, LayoutDoc, ParameterDefinition, ParameterValue, ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { WidgetView } from '../widgets/WidgetView'

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
    />
  )
}
