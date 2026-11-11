import { DataTable } from './DataTable'
import { MatrixGrid } from './MatrixGrid'
import { useTheme, type ThemeChoice } from './theme'
import type { RenderedChart, RenderedMatrix, RenderedViewDocument } from './types'
import { validateRenderedView, type ViewIssue } from './validate'
import { VegaChart } from './VegaChart'

export interface RenderedViewsProps {
  /** A rendered view document, from anywhere: it is validated here, whatever its source. */
  document: unknown
  theme?: ThemeChoice
  /** Show each chart's data as a table under it. */
  showData?: boolean
}

function Problems({ title, items }: { title: string; items: Array<{ code: string; path?: string | null; message: string; suggestions?: string[] | null }> }) {
  return (
    <div className="vz-problems" role="alert" data-testid="vz-problems">
      <strong>{title}</strong>
      <ul>
        {items.map((e, i) => (
          <li key={i}>
            <code>{e.code}</code>
            {e.path ? <span className="vz-path">{` ${e.path}`}</span> : null}
            {`: ${e.message}`}
            {e.suggestions?.length ? ` Did you mean ${e.suggestions.join(', ')}?` : ''}
          </li>
        ))}
      </ul>
    </div>
  )
}

function Chart({ view, theme, showData }: { view: RenderedChart; theme: 'light' | 'dark'; showData: boolean }) {
  return (
    <>
      <VegaChart view={view} theme={theme} />
      {showData ? (
        <details className="vz-data">
          <summary>{`Data (${view.vega_lite.data.values.length} rows)`}</summary>
          <DataTable id={view.id} rows={view.vega_lite.data.values} />
        </details>
      ) : null}
    </>
  )
}

/**
 * The shared renderer: what a rendered view document holds, drawn. Matrices are grids, charts go through Vega-Lite, a failed document shows the server's errors in
 * words. A document that is not a rendered view, or whose chart is outside the subset or loads data, is refused here and nothing is drawn.
 */
export function RenderedViews({ document, theme = 'auto', showData = false }: RenderedViewsProps) {
  const resolved = useTheme(theme)
  const issues: ViewIssue[] = validateRenderedView(document)
  if (issues.length > 0) return <div className="vz" data-vz-theme={resolved}><Problems title="This is not a view that can be shown." items={issues} /></div>

  const rendered = document as RenderedViewDocument
  if (rendered.status === 'failed') return <div className="vz" data-vz-theme={resolved}><Problems title="The view could not be made." items={rendered.errors ?? []} /></div>

  return (
    <div className="vz" data-vz-theme={resolved} data-testid="vz-views">
      {(rendered.views ?? []).map((view) => (
        <figure key={view.id} className={`vz-view vz-view-${view.type}`} data-testid={`vz-view-${view.id}`}>
          {view.type === 'matrix' ? <MatrixGrid view={view as RenderedMatrix} /> : <Chart view={view as RenderedChart} theme={resolved} showData={showData} />}
        </figure>
      ))}
    </div>
  )
}
