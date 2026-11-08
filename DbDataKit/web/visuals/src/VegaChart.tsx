import { useEffect, useMemo, useRef, useState } from 'react'
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
export function VegaChart({ view, theme, box, density, legend }: { view: RenderedChart; theme: Theme; box?: Box; density?: DensityClass; legend?: 'show' | 'hide' }) {
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
    import('vega-embed')
      .then(({ default: embed }) =>
        embed(host.current!, spec as never, {
          actions: false,
          renderer: 'svg',
          config: chartConfig(theme) as never,
          loader: refusingLoader as never,
          ...safeVegaOptions,
        }),
      )
      .then((result) => {
        if (cancelled) result.finalize()
        else finalize = () => result.finalize()
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e))
      })
    return () => {
      cancelled = true
      finalize?.()
    }
  }, [spec, theme])

  const title = typeof view.vega_lite.title === 'string' ? view.vega_lite.title : `Chart ${view.id}`
  return (
    <div className="vz-chart" data-testid={`vz-chart-${view.id}`} style={box ? { width: box.width, height: box.height } : undefined}>
      <div ref={host} role="img" aria-label={title} />
      {error ? <p className="vz-error" role="alert">{`The chart could not be drawn: ${error}`}</p> : null}
    </div>
  )
}
