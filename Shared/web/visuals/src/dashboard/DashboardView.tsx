import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { placedWidgets } from '../client/fixtureClient'
import type { ViewsClient } from '../client/types'
import { useTheme, type ThemeChoice } from '../theme'
import type { ViewKind } from '../views/document'
import type { Box, LayoutDoc, ParameterValue, ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { selectLayout, type LayoutSelection, type Medium } from '../views/selectLayout'
import { PagedFrame } from '../report/PagedFrame'
import { ErrorTile } from '../widgets/states'
import { DashboardFrame } from './DashboardFrame'
import { drillOf, onSelectOf, resolveDrill, rowOf, selectFrom, type Hit, type HitHow, type ResolvedDrill } from './interaction'
import { browserVisibility, createRefresher, formatAsOf, parseEvery, systemClock, type Clock, type RefreshState, type Visibility } from './refresh'
import { changedParameters, defaultParameters, dependentWidgets, parametersKey, resolveLayout, type Resolution } from './resolution'

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
  /** The exact PDF in the viewer (a flow layout's Print preview, DBR15); without it that button says why it is disabled. */
  onPrintPreview?: () => void
  /**
   * A drill was followed (plan 32 section 3.5 item 3): where it goes and the parameters it gives, read from the clicked row. The host navigates (and checks the parameters against the target with `checkDrill`);
   * without a handler a widget's `drill` does nothing. A widget with an `on_select` too selects on a click and drills on Alt+click.
   */
  onDrill?: (drill: ResolvedDrill) => void
  /**
   * Kiosk mode (`?mode=kiosk`, plan 32 section 3.5 item 5): the layout the document's `kiosk.layout` names is shown unless `layout` names another, and the tiles have no maximize action. The host's chrome is the host's to hide.
   */
  kiosk?: boolean
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
export function DashboardView({ kind = 'dashboard', name, document, client, layout: requested, medium, box: given, theme = 'auto', parameters, onParameterChange, onSelect, onDrill, viewer, onPrintPreview, kiosk = false, refreshFloorMs, clock, visibility, random, timeZone }: DashboardViewProps) {
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
  const [failure, setFailure] = useState<string | null>(null)
  const latest = useRef<Resolution | null>(null)
  useEffect(() => {
    if (!chosenName) return
    const controller = new AbortController()
    let current = true
    setFailure(null)
    resolveLayout({ client, ref: { kind, name }, layout: chosenName, placed, parameters: values, previous: latest.current, document, signal: controller.signal }).then(
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
        const next = await resolveLayout({ client: l.client, ref: { kind: l.kind, name: l.name }, layout: l.chosenName, placed: l.placed, parameters: l.values, previous: latest.current, document: l.document, force: true })
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

  // pointing at a widget: a selection sets the parameter it names (the same change a control makes, so the dependents resolve again); a drill goes to the host
  const [notice, setNotice] = useState<string | null>(null)
  const interactive = onDrill !== undefined || Object.values(document.widgets).some((w) => onSelectOf(w) !== null)
  const hit = (id: string, at: Hit, how: HitHow) => {
    const widget = document.widgets[id]
    const row = rowOf(widget, latest.current?.results[id], at)
    setNotice(null)
    if (onDrill && drillOf(widget) && (how.drill || !onSelectOf(widget))) {
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
    body = <PagedFrame document={document} layout={chosen} box={box} theme={resolvedTheme} results={results} loading={loading} parameters={values} onParameterChange={change} asOf={resolution?.asOf} viewer={viewer} onPrintPreview={onPrintPreview} />
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
      />
    )
  return (
    <div ref={ref} className="vz-dashboard-host" data-testid="vz-dashboard-host" data-measuring={box ? undefined : ''} data-kiosk={kiosk ? '' : undefined} data-refresh={refreshing ? (refreshState?.paused ? 'paused' : refreshState?.stale ? 'stale' : 'on') : undefined} data-refresh-runs={refreshState?.runs} style={given ? { width: given.width, height: given.height } : { width: '100%', height: '100%' }}>
      {failure ? <ErrorTile code="resolve_failed" message={failure} /> : null}
      {body}
      {notice ? (
        <p className="vz-interaction-notice" role="alert" data-testid="vz-interaction-notice">
          {notice} <button type="button" className="vz-button" onClick={() => setNotice(null)}>Dismiss</button>
        </p>
      ) : null}
    </div>
  )
}
