import { useEffect, useMemo, useRef, useState } from 'react'
import { chartClick, type Hit, type HitHow } from './dashboard/interaction'
import { refusingLoader, safeVegaOptions } from './safety'
import { chartConfig, type Theme } from './theme'
import type { RenderedChart } from './types'
import type { Box } from './views/model'
import { fitChartSpec } from './widgets/chartFit'
import { densityClass, type DensityClass } from './widgets/density'

/**
 * A chart view, drawn by Vega-Lite through `vega-embed`. The spec has passed `validateRenderedView` (the caller's job: `RenderedViews` does it), and the embed is given a
 * loader that refuses everything and the syntax-tree interpreter, so even a spec the validator missed could neither fetch nor run code. No actions menu: the menu's
 * "open in the Vega editor" would send the spec, and its data, to a web site.
 *
 * Given a `box` (a dashboard tile) the chart is drawn **at that size** with `autosize: fit`, the spec's own size ignored, and the density class (chosen from the box unless given) takes away legend, axis titles
 * and ticks as the box shrinks (`fitChartSpec`). Without a box it draws at the theme's size, as a rendered view does.
 *
 * `vega-embed` is loaded when the first chart is drawn, so a page without a chart does not carry Vega.
 */
export function VegaChart({ view, theme, box, density, legend, onHit }: { view: RenderedChart; theme: Theme; box?: Box; density?: DensityClass; legend?: 'show' | 'hide'; onHit?: (hit: Hit, how: HitHow) => void }) {
  // the latest handler, read when a mark is clicked: a new handler does not draw the chart again
  const hit = useRef(onHit)
  hit.current = onHit
  const pointable = onHit !== undefined
  const host = useRef<HTMLDivElement>(null)
  const [error, setError] = useState<string | null>(null)
  const width = box?.width
  const height = box?.height
  const spec = useMemo(
    () => (width !== undefined && height !== undefined ? fitChartSpec(view.vega_lite, { width, height }, density ?? densityClass('chart', { width, height }), legend) : view.vega_lite),
    [view, width, height, density, legend],
  )

  useEffect(() => {
    let cancelled = false
    let finalize: (() => void) | undefined
    setError(null)
    // each draw has an element of its own: when the spec changes while an earlier embed is still on its way, the earlier one must not draw over (or clear) the later one's picture
    const mount = document.createElement('div')
    host.current?.appendChild(mount)
    import('vega-embed')
      .then(({ default: embed }) =>
        embed(mount, spec as never, {
          actions: false,
          renderer: 'svg',
          config: chartConfig(theme) as never,
          loader: refusingLoader as never,
          ...safeVegaOptions,
        }),
      )
      .then((result) => {
        if (cancelled) result.finalize()
        else {
          if (pointable) result.view.addEventListener('click', chartClick((h, how) => hit.current?.(h, how)))
          finalize = () => result.finalize()
        }
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e))
      })
    return () => {
      cancelled = true
      finalize?.()
      mount.remove()
    }
  }, [spec, theme, pointable])

  const title = typeof view.vega_lite.title === 'string' ? view.vega_lite.title : `Chart ${view.id}`
  return (
    <div className={pointable ? 'vz-chart vz-pointable' : 'vz-chart'} data-testid={`vz-chart-${view.id}`} style={box ? { width: box.width, height: box.height } : undefined}>
      <div ref={host} role="img" aria-label={title} />
      {error ? <p className="vz-error" role="alert">{`The chart could not be drawn: ${error}`}</p> : null}
    </div>
  )
}
