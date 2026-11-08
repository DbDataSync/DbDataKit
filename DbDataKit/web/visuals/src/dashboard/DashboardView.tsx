import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { placedWidgets } from '../client/fixtureClient'
import type { ViewsClient } from '../client/types'
import { useTheme, type ThemeChoice } from '../theme'
import type { ViewKind } from '../views/document'
import type { Box, LayoutDoc, ParameterValue, ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { selectLayout, type LayoutSelection, type Medium } from '../views/selectLayout'
import { ErrorTile } from '../widgets/states'
import { DashboardFrame } from './DashboardFrame'
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
 * reflow layout laid out by `layoutReflow` and redrawn when the container changes size (a `ResizeObserver`, or the `box` a test gives). Resizing across a selector switches layout and keeps the results of
 * the widgets both layouts place. A widget that fails shows its own error; a failure of the call itself shows once, for the page. A canvas or flow layout is the work of their own engines (DBR07, DBR14):
 * until then a note says so.
 */
export function DashboardView({ kind = 'dashboard', name, document, client, layout, medium, box: given, theme = 'auto', parameters, onParameterChange, onSelect }: DashboardViewProps) {
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
        onMaximize={setMaximized}
      />
    )
  return (
    <div ref={ref} className="vz-dashboard-host" data-testid="vz-dashboard-host" data-measuring={box ? undefined : ''} style={given ? { width: given.width, height: given.height } : { width: '100%', height: '100%' }}>
      {failure ? <ErrorTile code="resolve_failed" message={failure} /> : null}
      {body}
    </div>
  )
}
