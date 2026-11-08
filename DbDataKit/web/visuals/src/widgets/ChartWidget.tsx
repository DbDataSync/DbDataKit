import { VegaChart } from '../VegaChart'
import type { Theme } from '../theme'
import type { RenderedChart } from '../types'
import type { Box, WidgetOptionsDoc } from '../views/model'
import type { DensityClass } from './density'

/** A chart widget: the Vega-Lite view of its result drawn at the box of its tile (`VegaChart` with a box), SVG, the legend and titles by density. */
export function ChartWidget({ view, theme, box, density, options }: { view: RenderedChart; theme: Theme; box?: Box; density?: DensityClass; options?: WidgetOptionsDoc }) {
  return <VegaChart view={view} theme={theme} box={box} density={density} legend={options?.legend} />
}
