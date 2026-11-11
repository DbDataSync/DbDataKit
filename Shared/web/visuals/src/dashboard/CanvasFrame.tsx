import type { ReactNode } from 'react'
import type { Box, CanvasLayoutDoc } from '../views/model'
import { TILE_GUTTER } from './reflow'
import { layoutCanvas, type CanvasResult } from './canvas'
import { tileView, type TileContext } from './tile'

export interface CanvasFrameProps extends TileContext {
  layout: CanvasLayoutDoc
  /** The container in CSS pixels. */
  box: Box
  /** The tile that fills the whole container now (it leaves the canvas), and the action that sets or clears it (`onMaximize`). */
  maximized?: string | null
  /** Precomputed layout of this layout and box. */
  placed?: CanvasResult
}

/**
 * The canvas dashboard as markup (plan 32 section 3.4): the container (`vz-dashboard`, the box) holds a sizer as large as the scrolled area; in it the canvas element, at its own size in pixels, moved to
 * its offset and scaled as one picture by a CSS transform (`transform-origin: 0 0`), so text scales with it and charts, drawn as SVG by the renderer, stay sharp. Tiles sit at their canvas pixels in
 * ascending layer order. The container scrolls only where the picture is larger than the box (`min_scale` held, or `overflow: scroll`). A maximized tile leaves the canvas and fills the container at
 * the reflow density. No state and no effects: the box and the results are given.
 */
export function CanvasFrame({ box, maximized = null, placed, ...context }: CanvasFrameProps) {
  const { layout } = context
  const laid = placed ?? layoutCanvas(layout, context.document.widgets, box)
  const root = (mode: string, scrolls: boolean, children: ReactNode, extra: Record<string, string | number> = {}) => (
    <div className="vz vz-dashboard" data-vz-theme={context.theme} data-testid="vz-dashboard" data-layout={layout.name} data-layout-type="canvas" data-mode={mode} data-scrolls={scrolls ? '' : undefined} {...extra} style={{ width: box.width, height: box.height }}>
      {children}
    </div>
  )

  if (maximized !== null && layout.tiles.some((t) => t.widget === maximized))
    return root('maximized', false, <div className="vz-dashboard-maximized" data-testid="vz-maximized">{tileView(context, maximized, { width: box.width - TILE_GUTTER, height: box.height - TILE_GUTTER }, true)}</div>)

  return root(
    'canvas',
    laid.scrolls,
    <div className="vz-canvas-sizer" data-testid="vz-canvas-sizer" style={{ width: laid.content.width, height: laid.content.height }}>
      <div
        className="vz-canvas"
        data-testid="vz-canvas"
        style={{ left: laid.offset.x, top: laid.offset.y, width: laid.canvas.width, height: laid.canvas.height, transform: `scale(${laid.scale})` }}
      >
        {laid.tiles.map((t) => (
          <div key={t.widget} className="vz-canvas-tile" data-testid={`vz-tile-${t.widget}`} data-layer={t.layer} style={{ left: t.x, top: t.y, width: t.width, height: t.height, zIndex: t.layer }}>
            {tileView(context, t.widget, { width: t.width, height: t.height }, false)}
          </div>
        ))}
      </div>
    </div>,
    { 'data-scale': laid.scale.toFixed(4), 'data-overflow': laid.mode },
  )
}
