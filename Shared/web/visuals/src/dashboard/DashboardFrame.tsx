import type { ReactNode } from 'react'
import type { Theme } from '../theme'
import type { Box, LayoutDoc, ParameterValue, ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { CanvasFrame } from './CanvasFrame'
import { tileView, type TileContext } from './tile'
import { TILE_GUTTER, layoutReflow, type ReflowResult } from './reflow'

export interface DashboardFrameProps {
  document: Pick<ViewsDoc, 'widgets' | 'parameters'>
  /** The layout chosen by its selector. A reflow layout is drawn here, a canvas layout by `CanvasFrame`; a flow layout has its own engine (not built: a note says so). */
  layout: LayoutDoc
  /** The container in CSS pixels. */
  box: Box
  theme: Theme
  results: Record<string, RenderedWidget>
  /** Widgets whose rows are still on their way (they draw as loading). */
  loading?: ReadonlySet<string>
  parameters?: ParameterValues
  onParameterChange?: (parameter: string, value: ParameterValue) => void
  /** The tile that fills the whole container now, and the action that sets or clears it. */
  maximized?: string | null
  onMaximize?: (widget: string | null) => void
  /** Precomputed reflow layout of this layout and box (reflow only). */
  placed?: ReflowResult
  /** Pointing at a widget (cross-filter, drill) and the freshness shown in each tile: see `TileContext`. */
  onHit?: TileContext['onHit']
  asOf?: TileContext['asOf']
  stale?: boolean
  canDrill?: boolean
  sortable?: boolean
  onServerSort?: TileContext['onServerSort']
  onLink?: TileContext['onLink']
  onExpand?: TileContext['onExpand']
}

/**
 * The reflow dashboard as markup, from the layout, the box and the results (plan 32 section 3.3): CSS grid with `minmax(0, 1fr)` tracks, so the tiles fill the container with **no page scroll and no tile
 * scroll**; a tile below its minimum puts the whole layout on the **compact stack** (one column, minimum heights, the page scrolls: the only scrolling a reflow dashboard does). A table or matrix that does
 * not fit its tile says "12 of 40 rows" and can be **maximized** to the whole container, where it scrolls. No state and no effects: the box and the results are given, so a test draws any size.
 */
export function DashboardFrame({ document, layout, box, theme, results, loading, parameters = {}, onParameterChange, maximized = null, onMaximize, placed, onHit, asOf, stale, canDrill, sortable, onServerSort, onLink, onExpand }: DashboardFrameProps) {
  const root = (mode: string, children: ReactNode) => (
    <div className="vz vz-dashboard" data-vz-theme={theme} data-testid="vz-dashboard" data-layout={layout.name} data-layout-type={layout.type} data-mode={mode} style={{ width: box.width, height: box.height }}>
      {children}
    </div>
  )
  const context = { document, layout, theme, results, loading, parameters, onParameterChange, onMaximize, onHit, asOf, stale, canDrill, sortable, onServerSort, onLink, onExpand }
  if (layout.type === 'canvas') return <CanvasFrame {...context} layout={layout} box={box} maximized={maximized} />
  if (layout.type !== 'reflow')
    return root('none', <p className="vz-state vz-dashboard-notice" role="note" data-testid="vz-engine-missing">{`The layout \`${layout.name}\` is a ${layout.type} layout, which this view does not draw yet.`}</p>)

  const widgetView = (id: string, tileBox: Box, isMaximized: boolean) => tileView(context, id, tileBox, isMaximized)

  if (maximized !== null && layout.tiles.some((t) => t.widget === maximized))
    return root('maximized', <div className="vz-dashboard-maximized" data-testid="vz-maximized">{widgetView(maximized, { width: box.width - TILE_GUTTER, height: box.height - TILE_GUTTER }, true)}</div>)

  const laid = placed ?? layoutReflow(layout, document.widgets, box)
  const tileBox = (t: { width: number; height: number }): Box => ({ width: t.width - TILE_GUTTER, height: t.height - TILE_GUTTER })
  if (laid.mode === 'stack')
    return root('stack', (
      <div className="vz-dashboard-stack" style={{ height: laid.contentHeight }}>
        {laid.tiles.map((t) => (
          <div key={t.widget} className="vz-dashboard-tile" data-testid={`vz-tile-${t.widget}`} style={{ height: t.height, flex: 'none' }}>{widgetView(t.widget, tileBox(t), false)}</div>
        ))}
      </div>
    ))
  return root('grid', (
    <div className="vz-dashboard-grid" style={{ gridTemplateColumns: `repeat(${laid.columns}, minmax(0, 1fr))`, gridTemplateRows: `repeat(${laid.rows}, minmax(0, 1fr))` }}>
      {laid.tiles.map((t) => (
        <div key={t.widget} className="vz-dashboard-tile" data-testid={`vz-tile-${t.widget}`} style={{ gridColumn: `${t.cell!.column} / span ${t.cell!.columns}`, gridRow: `${t.cell!.row} / span ${t.cell!.rows}` }}>
          {widgetView(t.widget, tileBox(t), false)}
        </div>
      ))}
    </div>
  ))
}
