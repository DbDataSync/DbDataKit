import { View, parse, type Loader } from 'vega'
import { compile, type TopLevelSpec } from 'vega-lite'
import { refusingLoader, safeVegaOptions } from './safety'
import { chartConfig, type Theme } from './theme'

/**
 * A rendered chart as SVG text, with no browser: the same Vega-Lite compile, interpreter and refusing loader the component uses, drawn by Vega's own headless
 * renderer. Component tests snapshot it, and a report server can use it. The spec must already have passed `validateRenderedView`.
 */
export async function chartToSvg(vegaLite: Record<string, unknown>, theme: Theme = 'light'): Promise<string> {
  const { spec } = compile(vegaLite as unknown as TopLevelSpec, { config: chartConfig(theme) as never })
  const view = new View(parse(spec, {}, { ast: safeVegaOptions.ast }), { loader: refusingLoader as unknown as Loader, renderer: 'none', expr: safeVegaOptions.expr })
  try {
    return await view.toSVG()
  } finally {
    view.finalize()
  }
}
