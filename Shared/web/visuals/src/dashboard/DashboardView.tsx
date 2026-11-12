import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { CustomViewsContext, type CustomViewsContextValue, type CustomViewsHost } from '../custom/CustomWidgetFrame'
import { ActionDialog, type OpenAction } from '../links/ActionDialog'
import type { ActionHost, ActionOutcome } from '../links/actions'
import { LinkSurface, type OpenSurface } from '../links/LinkSurface'
import { surfaceAddress, thenWidgets } from '../links/surface'
import { describeLink, linkValues, type LinkInfo, type LinkScalar } from '../views/links'
import { placedWidgets } from '../client/fixtureClient'
import type { ResolveOptions, ServerSort, ViewsClient } from '../client/types'
import type { RenderedMatrix } from '../types'
import { useTheme, type ThemeChoice } from '../theme'
import type { ViewKind } from '../views/document'
import type { Box, LayoutDoc, ParameterDefinition, ParameterValue, ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { selectLayout, type LayoutSelection, type Medium } from '../views/selectLayout'
import { PagedFrame } from '../report/PagedFrame'
import { usePrintPreview } from '../report/PrintPreview'
import type { CellLink, RowExpand } from '../widgets/WidgetView'
import { ErrorTile } from '../widgets/states'
import { DashboardFrame } from './DashboardFrame'
import { clickOf, drillOf, onSelectOf, resolveDrill, rowOf, selectFrom, type Hit, type HitHow, type ResolvedDrill, type Row } from './interaction'
import { browserVisibility, createRefresher, formatAsOf, parseEvery, systemClock, type Clock, type RefreshState, type Visibility } from './refresh'
import { changedParameters, defaultParameters, dependentWidgets, parametersKey, resolveLayout, type Resolution } from './resolution'

/** A link a widget followed (plan 32 DBR31, DBR36): which widget, the link read, the values it gives for the row, and the target's address when the host has one. The host opens it (`as`: navigate, a dialog, a panel). */
export interface LinkEvent {
  widget: string
  link: LinkInfo
  values: Record<string, LinkScalar | LinkScalar[]>
  route: string | null
  /** The target's address with the values (and the locked names) in its query string; null when the host has no route for the target. */
  address: string | null
  row: Row
}

/**
 * What the host that serves the app gives so links open (plan 32 DBR36): the address of a link's target inside the app, and how a navigating link goes there. A link whose `as` is `dialog` or `panel` opens in the shell's
 * own surface (`LinkSurface`: a sandboxed frame over the page); a `navigate` link calls `navigate` (the page's router).
 */
export interface LinkHost {
  route(link: LinkInfo): string | null
  navigate?(address: string, event: LinkEvent): void
}

export interface DashboardViewProps {
  /** The document's kind and name: what the client resolves it by. */
  kind?: ViewKind
  name: string
  /** The document, already opened and valid (`ViewsClient.open` gives it). */
  document: ViewsDoc
  client: ViewsClient
  /** A layout named by the address (`?layout=print`), which wins over any selector. */
  layout?: string
  /** Where it is shown; `screen` by default. */
  medium?: Medium
  /** The container's size in CSS pixels, given: nothing is measured (tests, a host that knows). Without it the view fills its parent and follows it with a `ResizeObserver`. */
  box?: Box
  theme?: ThemeChoice
  /** The parameters now; without them the view keeps its own, starting from the document's defaults. */
  parameters?: ParameterValues
  onParameterChange?: (parameter: string, value: ParameterValue) => void
  /** Called when the selected layout changes (a window resized across a selector, a layout named), with how it was chosen. */
  onSelect?: (selection: Extract<LayoutSelection<LayoutDoc>, { ok: true }>) => void
  /** Who is looking: the `{viewer}` of a flow layout's header, footer and cover. */
  viewer?: string
  /** The exact PDF in the viewer (a flow layout's Print preview, DBR15); without it the view asks the client's `exportPdf` itself (the published report's PDF, DBR27), and without that the button says why it is disabled. */
  onPrintPreview?: () => void
  /**
   * A drill was followed (plan 32 section 3.5 item 3): where it goes and the parameters it gives, read from the clicked row. The host navigates (and checks the parameters against the target with `checkDrill`);
   * without a handler a widget's `drill` does nothing. A widget with an `on_select` too selects on a click and drills on Alt+click.
   */
  onDrill?: (drill: ResolvedDrill) => void
  /** Let viewers sort tables and matrices by clicking a header (plan 32 DBR45), on screen only. Off by default. */
  sortable?: boolean
  /** A table the server cut at its limit: run the widget again with `order` for this column. Without it the loaded rows are sorted and the notice says so. */
  onServerSort?: (widget: string, field: string | null, dir: 'asc' | 'desc' | null) => void
  /** A cell that names a link of the document was clicked (an attribute's cell in a table or a matrix, plan 32 DBR51): the link's name, the widget and the cell's place in its result. The host opens the link's target (DBR36) or asks for the action (DBR53); without a handler such a cell is plain text. */
  onCellLink?: (link: CellLink) => void
  /**
   * Kiosk mode (`?mode=kiosk`, plan 32 section 3.5 item 5): the layout the document's `kiosk.layout` names is shown unless `layout` names another, and the tiles have no maximize action. The host's chrome is the host's to hide.
   */
  kiosk?: boolean
  /**
   * Where the code of a custom widget (`type: custom`) is served from (DBR31): without it a custom widget draws a note. The widget's frame sends back parameter changes (the same cross-filter as a control) and the links it follows.
   */
  customViews?: CustomViewsHost
  /**
   * The app's addresses for link targets (DBR36). With it a click on a widget with an `on_click` (and a custom widget's `open`) follows the document's link: a dialog or a side panel opens in a frame of its own, a navigating link goes
   * through `navigate`, and after a save `then` resolves the source widget (or the ones it names) again. Without it, and without `onLink`, a link does nothing.
   */
  linkHost?: LinkHost
  /**
   * What runs an action link (plan 32 DBR53): a click on a link with a `change` or a `task` opens the shell's confirmation popup (`ActionDialog`, in the declaration's words), and a confirmed call goes to the host, which
   * sends it to the server as the viewer. Without it (and without `onLink`) an action link does nothing but say so.
   */
  actionHost?: ActionHost
  /** A link a widget followed was resolved: the host opens it itself (`linkHost`'s surfaces are then not used). Without a handler a link does nothing. */
  onLink?: (event: LinkEvent) => void
  /** A dialog or panel a custom widget opened has closed (`saved` when a form saved): the widget is told. */
  closedLink?: CustomViewsContextValue['closed']
  /** The lowest refresh interval in milliseconds (the server's setting; 15 s by default). */
  refreshFloorMs?: number
  /** Time, for the refresh: the system's by default, a fake in tests. */
  clock?: Clock
  /** Whether the page is shown: the browser's by default; a hidden page is not refreshed. */
  visibility?: Visibility
  /** The jitter's source of randomness, [0, 1). */
  random?: () => number
  /** The time zone of the "as of hh:mm" in each tile (the viewer's by default). */
  timeZone?: string
}

const whole = (n: number) => Math.max(0, Math.round(n))

/** The size of the element this ref is on, following it with a `ResizeObserver`; the given box wins and nothing is observed. Null until measured. */
function useBox(given: Box | undefined) {
  const ref = useRef<HTMLDivElement>(null)
  const [measured, setMeasured] = useState<Box | null>(null)
  useLayoutEffect(() => {
    const element = ref.current
    if (given || !element) return
    const read = (width: number, height: number) =>
      setMeasured((old) => (old && old.width === whole(width) && old.height === whole(height) ? old : { width: whole(width), height: whole(height) }))
    read(element.clientWidth, element.clientHeight)
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver((entries) => {
      const rect = entries[entries.length - 1]?.contentRect
      if (rect) read(rect.width, rect.height)
    })
    observer.observe(element)
    return () => observer.disconnect()
  }, [given])
  return { ref, box: given ?? measured }
}

/**
 * A dashboard or report document drawn in its container (plan 32 DBR06): the layout its selector picks for the container (`selectLayout`), its widgets resolved through the `ViewsClient` as the viewer, a
 * reflow layout laid out by `layoutReflow` (a canvas by `layoutCanvas`) and redrawn when the container changes size (a `ResizeObserver`, or the `box` a test gives). Resizing across a selector switches layout and keeps the results of
 * the widgets both layouts place. A widget that fails shows its own error; a failure of the call itself shows once, for the page. A canvas layout is drawn by `layoutCanvas` (DBR07), scaled or scrolled in the same container; a flow layout is the work of its own engine (DBR14):
 * until then a note says so.
 */
export function DashboardView({ kind = 'dashboard', name, document, client, layout: requested, medium, box: given, theme = 'auto', parameters, onParameterChange, onSelect, onDrill, sortable = false, onServerSort, customViews, linkHost, actionHost, onLink, onCellLink, closedLink, viewer, onPrintPreview, kiosk = false, refreshFloorMs, clock, visibility, random, timeZone }: DashboardViewProps) {
  const layout = requested ?? (kiosk ? document.kiosk?.layout : undefined)
  const resolvedTheme = useTheme(theme)
  const { ref, box } = useBox(given)
  const defaults = useMemo(() => defaultParameters(document), [document])
  const [own, setOwn] = useState<ParameterValues>(defaults)
  const values = parameters ?? own
  const valuesKey = parametersKey(values)
  const change = useCallback((parameter: string, value: ParameterValue) => {
    onParameterChange?.(parameter, value)
    if (parameters === undefined) setOwn((old) => ({ ...old, [parameter]: value }))
  }, [onParameterChange, parameters])

  const selection = useMemo(() => (box ? selectLayout(document, { width: box.width, height: box.height, medium, layout }) : null), [document, box, medium, layout])
  const chosen = selection?.ok ? selection.layout : null
  const chosenName = chosen?.name ?? null
  const placed = useMemo(() => (chosenName ? placedWidgets(document, chosenName) : []), [document, chosenName])

  useEffect(() => {
    if (selection?.ok) onSelect?.(selection)
    // the callback is the host's; a new layout is the event
  }, [chosenName])

  const [resolution, setResolution] = useState<Resolution | null>(null)
  // the viewer's sorts of tables the server cut (DBR45): kept for every later request of this view, never stored
  const sortsRef = useRef<Record<string, ServerSort>>({})
  // the rows the viewer expanded in matrices (DBR55), by widget: kept for every later request of this view, never stored
  const expandRef = useRef<NonNullable<ResolveOptions['expand']>>({})
  const [failure, setFailure] = useState<string | null>(null)
  const latest = useRef<Resolution | null>(null)
  useEffect(() => {
    if (!chosenName) return
    const controller = new AbortController()
    let current = true
    setFailure(null)
    resolveLayout({ client, ref: { kind, name }, layout: chosenName, placed, parameters: values, previous: latest.current, document, order: sortsRef.current, expand: expandRef.current, signal: controller.signal }).then(
      (next) => {
        if (!current) return
        latest.current = next
        setResolution(next)
      },
      (e: unknown) => {
        if (current && !controller.signal.aborted) setFailure(e instanceof Error ? e.message : String(e))
      },
    )
    return () => {
      current = false
      controller.abort()
    }
    // `values` is read through its key: the same values are the same request
  }, [client, kind, name, document, chosenName, placed, valuesKey])

  // refresh (DBR08): the document's `refresh.every`, never below the floor, paused while the page is hidden; a failed refresh keeps what is shown and marks it stale
  const every = parseEvery(document.refresh?.every)
  const refreshing = every !== null && chosenName !== null
  const [refreshState, setRefreshState] = useState<RefreshState | null>(null)
  const live = useRef({ client, kind, name, document, chosenName, placed, values })
  live.current = { client, kind, name, document, chosenName, placed, values }
  useEffect(() => {
    if (!refreshing) {
      setRefreshState(null)
      return
    }
    const source = visibility ?? browserVisibility
    const refresher = createRefresher({
      everyMs: every,
      floorMs: refreshFloorMs,
      clock: clock ?? systemClock,
      random,
      hidden: source.hidden(),
      onChange: setRefreshState,
      run: async () => {
        const l = live.current
        if (!l.chosenName) return
        const next = await resolveLayout({ client: l.client, ref: { kind: l.kind, name: l.name }, layout: l.chosenName, placed: l.placed, parameters: l.values, previous: latest.current, document: l.document, force: true, order: sortsRef.current, expand: expandRef.current })
        latest.current = next
        setResolution(next)
      },
    })
    refresher.start()
    const off = source.subscribe(() => refresher.setHidden(source.hidden()))
    return () => {
      off()
      refresher.stop()
    }
    // a new layout or document starts the interval again; the clock, the source and the floor are the host's and fixed for a view
  }, [client, kind, name, document, chosenName, refreshing, every])

  // a click on a header of a table the server cut: the widget runs again with the column as `order` (the module's), the others keep what they show
  const serverSort = (widget: string, field: string | null, dir: 'asc' | 'desc' | null) => {
    onServerSort?.(widget, field, dir)
    const next = { ...sortsRef.current }
    if (field === null || dir === null) delete next[widget]
    else next[widget] = { by: field, dir }
    sortsRef.current = next
    const l = live.current
    if (!l.chosenName) return
    l.client.resolve({ kind: l.kind, name: l.name }, l.chosenName, l.values, { only: [widget], order: next }).then(
      (fresh) => {
        const base = latest.current
        if (!base) return
        const merged: Resolution = { ...base, results: { ...base.results, ...fresh.widgets }, asOfBy: { ...base.asOfBy, ...Object.fromEntries(Object.keys(fresh.widgets).map((id) => [id, fresh.as_of])) } }
        latest.current = merged
        setResolution(merged)
      },
      (e: unknown) => setNotice(`The table could not be sorted by the server: ${e instanceof Error ? e.message : String(e)}`),
    )
  }
  const serverSorting = client.sortsOnServer ? serverSort : onServerSort

  // a click on the toggle of a matrix row that can expand (DBR55): the widget runs again with the rows the viewer has open, the module reads the details of each as the viewer, the others keep what they show
  const toggleExpand = (e: RowExpand) => {
    const l = live.current
    if (!l.chosenName) return
    const open = (latest.current?.results[e.widget] as RenderedMatrix | undefined)?.rows?.flatMap((r) => (r.expanded && r.key ? [r.key] : [])) ?? []
    const current = expandRef.current[e.widget] ?? open
    const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b)
    const next = e.expanded ? [...current.filter((k) => !same(k, e.key)), e.key] : current.filter((k) => !same(k, e.key))
    expandRef.current = { ...expandRef.current, [e.widget]: next }
    l.client.resolve({ kind: l.kind, name: l.name }, l.chosenName, l.values, { only: [e.widget], order: sortsRef.current, expand: expandRef.current }).then(
      (fresh) => {
        const base = latest.current
        if (!base) return
        const merged: Resolution = { ...base, results: { ...base.results, ...fresh.widgets }, asOfBy: { ...base.asOfBy, ...Object.fromEntries(Object.keys(fresh.widgets).map((id) => [id, fresh.as_of])) } }
        latest.current = merged
        setResolution(merged)
      },
      (err: unknown) => setNotice(`The row could not be ${e.expanded ? 'expanded' : 'collapsed'}: ${err instanceof Error ? err.message : String(err)}`),
    )
  }

  // pointing at a widget: a selection sets the parameter it names (the same change a control makes, so the dependents resolve again); a drill goes to the host
  const [notice, setNotice] = useState<string | null>(null)
  const follows = linkHost !== undefined || onLink !== undefined || actionHost !== undefined
  const interactive = onDrill !== undefined || Object.values(document.widgets).some((w) => onSelectOf(w) !== null || (follows && clickOf(w) !== null))
  // a link followed (DBR36): the values for the row, the target's address, and what the host does with it: its own handler, a navigation, or the shell's dialog or panel
  const [surface, setSurface] = useState<{ widget: string; link: LinkInfo; open: OpenSurface } | null>(null)
  const [closedHere, setClosedHere] = useState<CustomViewsContextValue['closed']>(null)
  const [action, setAction] = useState<OpenAction | null>(null)
  const follow = (widget: string, link: LinkInfo, row: Row) => {
    if (link.action) {
      const event: LinkEvent = { widget, link, values: linkValues(link, row, values), route: null, address: null, row }
      if (onLink) { onLink(event); return }
      if (!actionHost) { setNotice(`The link \`${link.name}\` is an action, and this host cannot run actions.`); return }
      setAction({ widget, link: link as OpenAction['link'], row })
      return
    }
    const route = linkHost?.route(link) ?? customViews?.route?.(link) ?? null
    const linked = linkValues(link, row, values)
    const address = route === null ? null : surfaceAddress(route, linked, link.lock)
    const event: LinkEvent = { widget, link, values: linked, route, address, row }
    if (onLink) { onLink(event); return }
    if (!linkHost) return
    if (address === null) { setNotice(`The link \`${link.name}\` leads to the ${link.targetKind} \`${link.target}\`, which this host has no address for.`); return }
    if (link.as === 'navigate') { linkHost.navigate?.(address, event); return }
    setSurface({ widget, link, open: { as: link.as, label: link.label ?? `${link.targetKind}: ${link.target}`, address } })
  }
  // an action was accepted (applied, waiting for approval, or started): the popup says so and stays until it is closed; what `then` names resolves again at once, but not after a plain cancel
  const actionDone = (_outcome: ActionOutcome) => {
    const open = action
    if (!open) return
    const again = thenWidgets(open.link, open.widget, true).filter((w) => w in (latest.current?.results ?? {}))
    if (again.length > 0) resolveOnly(again)
    setClosedHere({ widget: open.widget, link: open.link.name, saved: true })
  }
  const closeSurface = (saved: boolean) => {
    const open = surface
    setSurface(null)
    if (!open) return
    setClosedHere({ widget: open.widget, link: open.link.name, saved })
    const again = thenWidgets(open.link, open.widget, saved).filter((w) => w in (latest.current?.results ?? {}))
    if (again.length > 0) resolveOnly(again)
  }
  // the widgets that resolve again after a save: the same call a sort makes, the other widgets keep what they show
  const resolveOnly = (ids: string[]) => {
    const l = live.current
    if (!l.chosenName) return
    l.client.resolve({ kind: l.kind, name: l.name }, l.chosenName, l.values, { only: ids, order: sortsRef.current }).then(
      (fresh) => {
        const base = latest.current
        if (!base) return
        const merged: Resolution = { ...base, results: { ...base.results, ...fresh.widgets }, asOfBy: { ...base.asOfBy, ...Object.fromEntries(Object.keys(fresh.widgets).map((id) => [id, fresh.as_of])) } }
        latest.current = merged
        setResolution(merged)
      },
      (e: unknown) => setNotice(`The page could not refresh after the change: ${e instanceof Error ? e.message : String(e)}`),
    )
  }
  // a cell that names a link (DBR51): the row the cell is in is what the link reads its values from
  const cellLink = (c: CellLink) => {
    const widget = document.widgets[c.widget]
    const doc = document.links?.[c.name]
    const row = rowOf(widget, latest.current?.results[c.widget], widget?.type === 'matrix' ? { kind: 'matrix', row: c.row, column: c.column } : { kind: 'row', index: c.row })
    setNotice(null)
    if (!doc) setNotice(`The widget \`${c.widget}\` follows the link \`${c.name}\`, which the document does not declare.`)
    else if (!row) setNotice('That cell has no row to take values from.')
    else follow(c.widget, describeLink(c.name, doc), row)
  }
  const hit = (id: string, at: Hit, how: HitHow) => {
    const widget = document.widgets[id]
    const row = rowOf(widget, latest.current?.results[id], at)
    setNotice(null)
    const clicked = follows ? clickOf(widget) : null
    if (clicked && (how.drill || !onSelectOf(widget))) {
      const doc = document.links?.[clicked]
      if (!doc) setNotice(`The widget \`${id}\` follows the link \`${clicked}\`, which the document does not declare.`)
      else if (!row) setNotice('That mark has no row to take values from.')
      else follow(id, describeLink(clicked, doc), row)
    } else if (onDrill && drillOf(widget) && (how.drill || !onSelectOf(widget))) {
      const out = resolveDrill(document, id, row, values)
      if (out.ok) onDrill(out.drill)
      else setNotice(out.message)
    } else if (onSelectOf(widget)) {
      const out = selectFrom(document, id, row, values, how)
      if (out.ok) change(out.parameter, out.value)
      else setNotice(out.message)
    }
  }
  const asOf = useMemo(() => {
    if (!refreshing || !resolution) return undefined
    const shown: Record<string, string> = {}
    for (const [id, at] of Object.entries(resolution.asOfBy)) {
      const type = document.widgets[id]?.type
      if (type === 'metric' || type === 'table' || type === 'matrix' || type === 'chart') shown[id] = formatAsOf(at, timeZone)
    }
    return shown
  }, [refreshing, resolution, document, timeZone])

  // custom widgets (DBR31): the frames reach the document only through these two, and the document's rules decide
  const custom = useMemo<CustomViewsContextValue | null>(() => {
    if (!customViews) return null
    return {
      host: customViews,
      links: document.links,
      definitions: document.parameters as ParameterDefinition[] | undefined,
      parameters: values,
      medium: medium === 'print' ? 'print' : 'screen',
      scheme: resolvedTheme,
      setParam: (parameter, value) => { setNotice(null); change(parameter, value) },
      open: (widget, link, row) => { setNotice(null); follow(widget, link, row as Row) },
      notice: setNotice,
      closed: closedLink ?? closedHere,
    }
    // `change` keeps the document's own parameters when none are given; the key stands for the values; `follow` reads the latest through the refs and state it closes over each render
  }, [customViews, linkHost, document, valuesKey, medium, resolvedTheme, onLink, closedLink, closedHere, change])

  const preview = usePrintPreview({ client, kind, name, layout: chosenName, parameters: values })
  const [maximized, setMaximized] = useState<string | null>(null)
  useEffect(() => setMaximized(null), [chosenName])

  const fresh = resolution !== null && resolution.key === valuesKey
  const results: Record<string, RenderedWidget> = resolution?.results ?? {}
  // while an answer is awaited only the widgets that use a changed parameter (or have no result) show as loading
  const loading = useMemo(() => {
    const stale = new Set(fresh || !resolution ? [] : dependentWidgets(document, changedParameters(resolution.parameters, values)))
    return new Set(placed.filter((id) => !(id in results) || stale.has(id)))
  }, [document, placed, fresh, resolution, results, values])

  let body
  if (!box) body = null
  else if (selection && !selection.ok) body = <ErrorTile code="no_layout" message={selection.refusal} />
  else if (chosen?.type === 'flow')
    body = <PagedFrame document={document} layout={chosen} box={box} theme={resolvedTheme} results={results} loading={loading} parameters={values} onParameterChange={change} asOf={resolution?.asOf} viewer={viewer} onPrintPreview={onPrintPreview ?? preview.onPrintPreview} previewUnavailable={onPrintPreview ? undefined : preview.unavailable} />
  else if (chosen)
    body = (
      <DashboardFrame
        document={document}
        layout={chosen}
        box={box}
        theme={resolvedTheme}
        results={results}
        loading={loading}
        parameters={values}
        onParameterChange={change}
        maximized={maximized}
        onMaximize={kiosk ? undefined : setMaximized}
        onHit={interactive ? hit : undefined}
        asOf={asOf}
        stale={refreshState?.stale}
        canDrill={onDrill !== undefined}
        canFollow={follows}
        sortable={sortable}
        onServerSort={serverSorting}
        onLink={onCellLink ?? (follows ? cellLink : undefined)}
        onExpand={client.expandsOnServer ? toggleExpand : undefined}
      />
    )
  const modalOpen = surface?.open.as === 'dialog' || action !== null
  const framed = (
    <div ref={ref} className="vz-dashboard-host" inert={modalOpen ? true : undefined} data-testid="vz-dashboard-host" data-measuring={box ? undefined : ''} data-kiosk={kiosk ? '' : undefined} data-refresh={refreshing ? (refreshState?.paused ? 'paused' : refreshState?.stale ? 'stale' : 'on') : undefined} data-refresh-runs={refreshState?.runs} style={given ? { width: given.width, height: given.height } : { width: '100%', height: '100%' }}>
      {failure ? <ErrorTile code="resolve_failed" message={failure} /> : null}
      {body}
      {preview.element}
      {notice ? (
        <p className="vz-interaction-notice" role="alert" data-testid="vz-interaction-notice">
          {notice} <button type="button" className="vz-button" onClick={() => setNotice(null)}>Dismiss</button>
        </p>
      ) : null}
    </div>
  )
  const shown = (
    <>
      {framed}
      {surface ? <LinkSurface key={surface.open.address} surface={surface.open} onClose={closeSurface} /> : null}
      {action && actionHost ? <ActionDialog key={`${action.widget}:${action.link.name}`} open={action} parameters={values} host={actionHost} onDone={actionDone} onCancel={() => setAction(null)} /> : null}
    </>
  )
  return custom ? <CustomViewsContext.Provider value={custom}>{shown}</CustomViewsContext.Provider> : shown
}
