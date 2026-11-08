import type { ReactNode } from 'react'
import { useTheme, type ThemeChoice } from '../theme'
import type { RenderedChart, RenderedMatrix } from '../types'
import type { Box, ParameterDefinition, ParameterValue, ParameterValues, WidgetDoc, WidgetOptionsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { ChartWidget } from './ChartWidget'
import { ImageBlock } from './ImageBlock'
import { MatrixWidget } from './MatrixWidget'
import { MetricTile } from './MetricTile'
import { ControlWidget } from './ParameterControls'
import { TableWidget } from './TableWidget'
import { TextBlock } from './TextBlock'
import { EmptyTile, ErrorTile, LoadingTile } from './states'
import { bodyBox, densityClass, type DensityClass } from './density'

export interface WidgetViewProps {
  /** The widget's id in its document. */
  id: string
  widget: WidgetDoc
  /** What the client resolved for it; none while it loads. A text widget needs none. */
  result?: RenderedWidget
  loading?: boolean
  /** The tile's box in CSS pixels. Without one nothing is cut and a chart draws at the theme's size. */
  box?: Box
  theme?: ThemeChoice
  /** The layout's `widget_options` for this widget. */
  options?: WidgetOptionsDoc
  parameters?: ParameterValues
  definitions?: ParameterDefinition[]
  onParameterChange?: (parameter: string, value: ParameterValue) => void
  /** Whether the tile fills the whole container now (the widget scrolls), and the action that toggles it. */
  maximized?: boolean
  onMaximize?: () => void
}

function isEmpty(result: RenderedWidget): boolean {
  switch (result.type) {
    case 'metric': return result.value === null || result.value === undefined
    case 'table': return result.rows.length === 0
    case 'matrix': return result.rows.length === 0
    case 'chart': return result.vega_lite.data.values.length === 0
    default: return false
  }
}

/**
 * One widget in its tile: a title bar (a metric carries its label instead), the body its type draws from its resolved result, and the three states besides drawn: **loading**, **empty** ("No data."), and
 * **error** (the server's code and words: one failing widget never fails the page). Light and dark by the theme; `density` is chosen from the box (a layout's `widget_options` can pin it).
 */
export function WidgetView({ id, widget, result, loading = false, box, theme = 'auto', options, parameters = {}, definitions = [], onParameterChange, maximized = false, onMaximize }: WidgetViewProps) {
  const resolved = useTheme(theme)
  const density: DensityClass = box ? densityClass(widget.type, box, options?.density) : options?.density === 'compact' ? 's' : 'l'
  const titled = widget.title !== undefined && widget.type !== 'metric' && options?.show_title !== false
  const body = box ? bodyBox(box, titled) : undefined
  const maximizable = onMaximize !== undefined && titled && (widget.type === 'table' || widget.type === 'matrix' || widget.type === 'chart')
  const titleId = `vz-title-${id}`

  const control = (value?: RenderedWidget) =>
    widget.type === 'control' ? <ControlWidget id={id} widget={widget} definition={definitions.find((d) => d.name === widget.parameter)} result={value as never} value={parameters[widget.parameter]} onChange={onParameterChange} /> : null

  let content: ReactNode
  if (result === undefined) {
    // text and a control draw from the document alone, so they have a body while the others wait
    if (widget.type === 'text') content = <TextBlock widget={widget} parameters={parameters} />
    else if (widget.type === 'control') content = control()
    else content = loading ? <LoadingTile what={widget.title} /> : <EmptyTile message="Nothing to show yet." />
  }
  else if (result.type === 'error') content = <ErrorTile code={result.code} message={result.message} path={result.path} suggestions={result.suggestions} />
  else if (result.type !== widget.type) content = <ErrorTile code="result_type_mismatch" message={`The ${widget.type} widget \`${id}\` was given the result of a ${result.type}.`} />
  else if (isEmpty(result)) content = <EmptyTile />
  else {
    switch (widget.type) {
      case 'metric': content = <MetricTile widget={widget} result={result as never} density={density} />; break
      case 'table': content = <TableWidget id={id} widget={widget} result={result as never} box={body} maximized={maximized} onMaximize={onMaximize} />; break
      case 'matrix': content = <MatrixWidget view={result as RenderedMatrix} box={body} maximized={maximized} onMaximize={onMaximize} title={widget.title} />; break
      case 'chart': content = <ChartWidget view={result as RenderedChart} theme={resolved} box={body} density={density} options={options} />; break
      case 'text': content = <TextBlock widget={widget} parameters={parameters} />; break
      case 'image': content = <ImageBlock widget={widget} result={result as never} />; break
      case 'control': content = control(result); break
    }
  }

  return (
    <section
      className={`vz vz-widget vz-w-${widget.type} vz-d-${density}${maximized ? ' vz-is-maximized' : ''}`}
      data-vz-theme={resolved}
      data-density={density}
      data-testid={`vz-widget-${id}`}
      aria-labelledby={titled ? titleId : undefined}
      aria-label={titled ? undefined : widget.type === 'metric' ? undefined : `${widget.type} ${id}`}
      aria-busy={result === undefined && loading ? true : undefined}
    >
      {titled ? (
        <header className="vz-widget-title">
          <h2 id={titleId}>{widget.title}</h2>
          {maximizable ? (
            <button type="button" className="vz-button" onClick={onMaximize} aria-pressed={maximized} aria-label={maximized ? `Close the full view of ${widget.title}` : `Maximize ${widget.title}`}>
              {maximized ? 'Close' : 'Maximize'}
            </button>
          ) : null}
        </header>
      ) : null}
      <div className="vz-widget-body">{content}</div>
    </section>
  )
}
