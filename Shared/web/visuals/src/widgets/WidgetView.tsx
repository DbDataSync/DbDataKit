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
import { drillOf, howOf, onSelectOf, selectedOf, parameterDefault, type Hit, type HitHow } from '../dashboard/interaction'

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
  /** The widget was pointed at (plan 32 DBR08): a chart's mark, a table's row, a matrix's label or cell. Only a widget with an `on_select` or a `drill` can be; without a handler none is. */
  onHit?: (hit: Hit, how: HitHow) => void
  /** When the rows were read (hh:mm, as the host formats it) and whether the last refresh failed: shown small in the tile when given. */
  asOf?: string
  stale?: boolean
  /** Whether the host follows a `drill` (default yes): without it a widget with only a `drill` is not pointable. */
  canDrill?: boolean
  /** Viewers sort a table or a matrix by clicking a header (plan 32 DBR45), on screen only; off by default so print, export and a host that does not say so are unchanged. */
  sortable?: boolean
  /** A table the server cut at its limit: the host runs it again with `order` (column field, direction; null, null for the document's order). */
  onServerSort?: (widget: string, field: string | null, dir: 'asc' | 'desc' | null) => void
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
export function WidgetView({ id, widget, result, loading = false, box, theme = 'auto', options, parameters = {}, definitions = [], onParameterChange, maximized = false, onMaximize, onHit, asOf, stale = false, canDrill = true, sortable = false, onServerSort }: WidgetViewProps) {
  const resolved = useTheme(theme)
  const density: DensityClass = box ? densityClass(widget.type, box, options?.density) : options?.density === 'compact' ? 's' : 'l'
  const titled = widget.title !== undefined && widget.type !== 'metric' && options?.show_title !== false
  const body = box ? bodyBox(box, titled) : undefined
  const maximizable = onMaximize !== undefined && titled && (widget.type === 'table' || widget.type === 'matrix' || widget.type === 'chart')
  const titleId = `vz-title-${id}`

  // pointing: only a widget that says what it does when selected or drilled is pointable
  const on = onSelectOf(widget)
  const pointable = onHit !== undefined && (on !== null || (canDrill && drillOf(widget) !== null)) && (widget.type === 'chart' || widget.type === 'table' || widget.type === 'matrix')
  const chosen = selectedOf(widget, parameters)
  const clear = () => {
    if (!on) return
    const definition = definitions.find((d) => d.name === on.set)
    onParameterChange?.(on.set, definition ? parameterDefault(definition) : null)
  }
  const chip =
    on && chosen.length > 0 ? (
      <span className="vz-selection" data-testid={`vz-selection-${id}`}>
        <span>{`${definitions.find((d) => d.name === on.set)?.label ?? on.set}: ${chosen.join(', ')}`}</span>
        {onParameterChange ? <button type="button" className="vz-button" data-testid={`vz-selection-clear-${id}`} onClick={clear} aria-label={`Clear the selection of ${on.set}`}>Clear</button> : null}
      </span>
    ) : null

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
      case 'table': content = <TableWidget id={id} widget={widget} result={result as never} box={body} maximized={maximized} onMaximize={onMaximize} sortable={sortable} onServerSort={onServerSort ? (field, dir) => onServerSort(id, field, dir) : undefined} onRow={pointable ? (index, m) => onHit!({ kind: 'row', index }, howOf(m)) : undefined} />; break
      case 'matrix': content = <MatrixWidget view={result as RenderedMatrix} box={body} maximized={maximized} onMaximize={onMaximize} title={widget.title} sortable={sortable} onCell={pointable ? (row, column, m) => onHit!({ kind: 'matrix', row, ...(column === undefined ? {} : { column }) }, howOf(m)) : undefined} />; break
      case 'chart': content = <ChartWidget view={result as RenderedChart} theme={resolved} box={body} density={density} options={options} onHit={pointable ? onHit : undefined} />; break
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
          {chip}
          {maximizable ? (
            <button type="button" className="vz-button" onClick={onMaximize} aria-pressed={maximized} aria-label={maximized ? `Close the full view of ${widget.title}` : `Maximize ${widget.title}`}>
              {maximized ? 'Close' : 'Maximize'}
            </button>
          ) : null}
        </header>
      ) : null}
      {titled ? null : chip}
      <div className="vz-widget-body">{content}</div>
      {asOf ? <span className={stale ? 'vz-asof vz-stale' : 'vz-asof'} data-testid={`vz-asof-${id}`}>{stale ? `as of ${asOf}, stale` : `as of ${asOf}`}</span> : null}
    </section>
  )
}
