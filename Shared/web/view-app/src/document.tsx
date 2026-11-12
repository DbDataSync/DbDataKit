import { DashboardView, type ClientDiagnostic, type LinkHost, type OpenSurface, type ParameterValue, type ParameterValues, type ViewKind, type ViewsClient, type ViewsDoc as BoardDoc } from '@dbdatakit/visuals'
import { ErrorNote, useLoad } from '@dbdatakit/store-ui'
import { useMemo, useState } from 'react'
import { LAYOUT_PARAM, parametersFromQuery, queryFromParameters, targetFile } from './boards'
import { useScheme } from './scheme'
import { routeOf, type ViewsDoc } from './spec'

export interface DocumentScreenProps {
  /** The file under `views` the document is in, as the manifest lists it: what the views client reads it by. */
  file: string
  kind: ViewKind
  views: ViewsClient
  /** The route's query: the document's parameters by name (an undeclared name is ignored) and `_layout`. */
  query: Record<string, string>
  /** The app's files, for the targets of the document's links. */
  doc: Pick<ViewsDoc, 'files' | 'views'>
  /** Tells the shell the route the person is on (their parameter choices), so the address and a reload keep them; the screen is not started again. */
  route: (path: string) => void
  /** Goes to another view of the app (a navigating link). */
  go: (route: string) => void
  /**
   * Opens a dialog or a panel (a link's `as`): the shell page owns the surface, because a frame inside this document cannot get a token (plan 32 DBR36). The default goes to the target as a page.
   */
  openSurface?: (surface: OpenSurface, closed: (saved: boolean) => void) => void
}

const where = (d: ClientDiagnostic) => [d.file, d.line, d.column].filter((p) => p !== undefined).join(':')

/**
 * A dashboard or a report of the app (plan 32 DBR13), drawn by the shared `DashboardView` through the app's views client: the document is read from the app's data route and every widget is resolved **as the signed-in person**
 * (the token the shell brokered). Parameters live in the route (`/<file>?region=West`), a change of one is written back to the shell's address and only the widgets that use it run again; a link goes to a file of the app.
 */
export function DocumentScreen({ file, kind, views, query, doc, route, go, openSurface }: DocumentScreenProps) {
  const opened = useLoad(() => views.open(kind, file), [views, kind, file])
  if (opened.error) return <ErrorNote error={opened.error} />
  if (!opened.data) return <div className="sui-loading" aria-busy="true">Loading…</div>
  const document = opened.data.document
  if (!document) {
    const errors = opened.data.diagnostics.filter((d) => d.severity === 'error')
    return (
      <div className="sui-error" role="alert" data-testid="va-document-problems">
        <b>{file} does not read:</b>
        <ul>{errors.map((d, i) => <li key={i}><code>{where(d)}</code> {d.message}</li>)}</ul>
      </div>
    )
  }
  return <Board file={file} kind={kind} views={views} document={document} query={query} doc={doc} route={route} go={go} openSurface={openSurface} />
}

function Board({ file, kind, views, document, query, doc, route, go, openSurface }: DocumentScreenProps & { document: BoardDoc }) {
  const scheme = useScheme()
  const definitions = document.parameters
  const [parameters, setParameters] = useState<ParameterValues>(() => parametersFromQuery(query, definitions))
  const layout = query[LAYOUT_PARAM] && document.layouts.some((l) => l.name === query[LAYOUT_PARAM]) ? query[LAYOUT_PARAM] : undefined
  const linkHost = useMemo<LinkHost>(() => ({
    route: (link) => { const target = targetFile(doc, link); return target ? routeOf(target) : null },
    navigate: (address) => go(address),
  }), [doc, go])
  const change = (parameter: string, value: ParameterValue) => {
    const next = { ...parameters, [parameter]: value }
    setParameters(next)
    route(routeOf(file, queryFromParameters(definitions, next, layout)))
  }
  return (
    <div className="va-board" data-testid="va-board" data-kind={kind}>
      <DashboardView
        kind={kind} name={file} document={document} client={views} theme={scheme} layout={layout}
        parameters={parameters} onParameterChange={change} sortable linkHost={linkHost}
        openSurface={openSurface ?? ((surface) => go(surface.address))}
      />
    </div>
  )
}
