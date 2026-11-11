// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureClient, type FixtureBundle } from '../client/fixtureClient'
import { validateText } from '../client/text'
import type { ViewsClient } from '../client/types'
import { fakeClock } from '../testing/fakeClock'
import type { Box, ParameterValues, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { DashboardView, type DashboardViewProps } from './DashboardView'
import type { ResolvedDrill } from './interaction'
import type { Visibility } from './refresh'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

// One board, two layouts over it: `desk` (a reflow grid, the default) and `wall` (a canvas). A matrix cross-filters (`on_select` sets `region`); a table drills; a metric and the table use `region`.
const text = `
schemaVersion: 1
kind: dashboard
title: Board
parameters:
  - {name: region, type: string, for: customers.region, many: true, default: [], label: Region}
refresh: {every: 5m}
kiosk: {layout: wall}
widgets:
  regions: {type: control, parameter: region, control: multiselect, label: Region, values: {attribute: customers.region}}
  net:
    type: metric
    label: Net sales
    encodings: {value: sales.net_amount}
    query: {model: retail_sales, filters: [{attribute: customers.region, operator: equals, values: {param: region}}]}
  by_region:
    type: matrix
    title: Net by region
    encodings: {rows: [customers.region], columns: [products.category], values: [{field: sales.net_amount}]}
    query: {model: retail_sales}
    on_select: {set: region, from: customers.region}
    drill: {to: {dashboard: detail}, with: {region: $customers.region}}
  top:
    type: table
    title: Top products
    encodings: {columns: [{field: customers.region, label: Region}, {field: sales.units, label: Units}]}
    query: {model: retail_sales, filters: [{attribute: customers.region, operator: equals, values: {param: region}}]}
    drill: {to: {dashboard: detail}, with: {region: $customers.region, year: $params.region}}
  note: {type: text, text: "Region: {{params.region}}"}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 8}
    tiles:
      - {widget: regions, x: 0, y: 0, w: 12, h: 1}
      - {widget: net, x: 0, y: 1, w: 4, h: 2}
      - {widget: note, x: 4, y: 1, w: 8, h: 2}
      - {widget: by_region, x: 0, y: 3, w: 6, h: 5}
      - {widget: top, x: 6, y: 3, w: 6, h: 5}
  - name: wall
    type: canvas
    when: {width: {min: 2400}}
    canvas: {width: 1920, height: 1080}
    tiles:
      - {widget: regions, x: 40, y: 30, w: 1840, h: 70}
      - {widget: net, x: 40, y: 120, w: 440, h: 200}
      - {widget: note, x: 500, y: 120, w: 1380, h: 200}
      - {widget: by_region, x: 40, y: 340, w: 900, h: 700}
      - {widget: top, x: 960, y: 340, w: 920, h: 700}
`
const document_ = validateText(text).document as ViewsDoc
const results: Record<string, RenderedWidget> = {
  regions: { type: 'control', values: [{ value: 'West' }, { value: 'East' }] },
  net: { type: 'metric', value: { v: 3300000, text: '$3.30M' } },
  by_region: {
    id: 'by_region',
    type: 'matrix',
    columns: [{ labels: ['Apparel'] }, { labels: ['Footwear'] }],
    rows: [
      { labels: ['West'], cells: [{ text: '10' }, { text: '20' }] },
      { labels: ['East'], cells: [{ text: '30' }, { text: '40' }] },
    ],
  },
  top: {
    type: 'table',
    columns: [{ field: 'customers.region', label: 'Region' }, { field: 'sales.units', label: 'Units' }],
    rows: [
      { cells: [{ v: 'West', text: 'West' }, { v: 90, text: '90' }] },
      { cells: [{ v: 'East', text: 'East' }, { v: 70, text: '70' }] },
    ],
  },
}
const bundle: FixtureBundle = { documents: { 'dashboard/board': text }, results: { 'dashboard/board': results } }

class FakeObserver {
  static all = new Set<FakeObserver>()
  readonly callback: (entries: Array<{ contentRect: { width: number; height: number } }>) => void
  constructor(callback: (entries: Array<{ contentRect: { width: number; height: number } }>) => void) {
    this.callback = callback
    FakeObserver.all.add(this)
  }
  observe() {}
  unobserve() {}
  disconnect() { FakeObserver.all.delete(this) }
}
let size: Box = { width: 1366, height: 768 }
async function resize(width: number, height: number) {
  size = { width, height }
  await act(async () => { for (const o of FakeObserver.all) o.callback([{ contentRect: { width, height } }]) })
}
let host: HTMLDivElement
let root: Root
beforeEach(() => {
  size = { width: 1366, height: 768 }
  FakeObserver.all.clear()
  ;(globalThis as { ResizeObserver?: unknown }).ResizeObserver = FakeObserver
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get: () => size.width })
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, get: () => size.height })
  host = window.document.createElement('div')
  window.document.body.appendChild(host)
  root = createRoot(host)
})
afterEach(async () => {
  await act(async () => root.unmount())
  host.remove()
})

interface Call { layout: string; widgets: string[]; parameters: ParameterValues }
function counting(over: Partial<ViewsClient> = {}) {
  const calls: Call[] = []
  const client: ViewsClient = { ...fixtureClient(bundle, { onResolve: (c) => calls.push({ layout: c.layout, widgets: c.widgets, parameters: c.parameters }) }), ...over }
  return { calls, client }
}
async function show(props: Partial<DashboardViewProps> & { client: ViewsClient }) {
  await act(async () => root.render(<DashboardView name="board" document={document_} theme="light" {...props} />))
}
const q = (id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLElement | null
const layoutNow = () => q('vz-dashboard')?.getAttribute('data-layout')
const click = async (el: Element, init: MouseEventInit = {}) => act(async () => { el.dispatchEvent(new window.MouseEvent('click', { bubbles: true, ...init })) })
const rowLabel = (name: string) => [...host.querySelectorAll('[data-testid="vz-matrix-by_region"] th[scope="row"]')].find((e) => e.textContent === name)!
const tableRow = (name: string) => [...host.querySelectorAll('[data-testid="vz-table-top"] tbody tr')].find((e) => e.textContent!.startsWith(name))!
const DEPENDENTS = ['net', 'note', 'top']

describe('cross-filter: on_select sets a declared parameter and only its dependents resolve again', () => {
  it('on a reflow layout: select, switch the selection, clear', async () => {
    const { calls, client } = counting()
    await show({ client })
    expect(calls.length).toBe(1)
    expect(q('vz-selection-by_region')).toBeNull()
    await click(rowLabel('West'))
    expect(calls.length).toBe(2)
    expect(calls[1]).toMatchObject({ layout: 'desk', parameters: { region: ['West'] } })
    expect([...calls[1]!.widgets].sort()).toEqual([...DEPENDENTS].sort())
    expect(q('vz-selection-by_region')!.textContent).toContain('Region: West')
    expect(q('vz-text')!.textContent).toContain('West')
    await click(rowLabel('East'))
    expect(calls[2]!.parameters).toEqual({ region: ['East'] })
    expect([...calls[2]!.widgets].sort()).toEqual([...DEPENDENTS].sort())
    // the same value again clears it
    await click(rowLabel('East'))
    expect(calls[3]!.parameters).toEqual({ region: [] })
    expect(q('vz-selection-by_region')).toBeNull()
    // nothing but the dependents was ever asked for after the first call
    expect(calls.slice(1).every((c) => !c.widgets.includes('by_region') && !c.widgets.includes('regions'))).toBe(true)
  })

  it('the Clear button of the chip and shift-click on a second value', async () => {
    const { calls, client } = counting()
    await show({ client })
    await click(rowLabel('West'))
    await click(rowLabel('East'), { shiftKey: true })
    expect(calls[2]!.parameters).toEqual({ region: ['West', 'East'] })
    expect(q('vz-selection-by_region')!.textContent).toContain('West, East')
    await click(q('vz-selection-clear-by_region')!)
    expect(calls[3]!.parameters).toEqual({ region: [] })
    expect(q('vz-selection-by_region')).toBeNull()
  })

  it('a selection made by a click is the same change a control makes: the host\'s parameters are told', async () => {
    const { client } = counting()
    const told: Array<[string, unknown]> = []
    await show({ client, parameters: { region: [] }, onParameterChange: (p, v) => told.push([p, v]) })
    await click(rowLabel('West'))
    expect(told).toEqual([['region', ['West']]])
  })

  it('the same story passes on a canvas layout, and the selection survives a switch between the two', async () => {
    const { calls, client } = counting()
    await show({ client, layout: 'wall' })
    expect(layoutNow()).toBe('wall')
    expect(calls.length).toBe(1)
    await click(rowLabel('West'))
    expect([...calls[1]!.widgets].sort()).toEqual([...DEPENDENTS].sort())
    expect(calls[1]!.parameters).toEqual({ region: ['West'] })
    expect(q('vz-selection-by_region')!.textContent).toContain('West')
    // to the reflow layout and back: the parameters and the chip stay, and nothing is asked again (every widget was resolved)
    await show({ client, layout: 'desk' })
    expect(layoutNow()).toBe('desk')
    expect(q('vz-selection-by_region')!.textContent).toContain('West')
    expect(calls.length).toBe(2)
    await click(rowLabel('East'))
    expect(calls[2]!.layout).toBe('desk')
    expect(calls[2]!.parameters).toEqual({ region: ['East'] })
    await show({ client, layout: 'wall' })
    expect(layoutNow()).toBe('wall')
    expect(q('vz-selection-by_region')!.textContent).toContain('East')
    expect(calls.length).toBe(3)
    await click(q('vz-selection-clear-by_region')!)
    expect(calls[3]).toMatchObject({ layout: 'wall', parameters: { region: [] } })
    expect([...calls[3]!.widgets].sort()).toEqual([...DEPENDENTS].sort())
    expect(q('vz-selection-by_region')).toBeNull()
  })

  it('a total row stands for no one value: its label cell is not pointable', async () => {
    const total = { id: 'by_region', type: 'matrix', columns: [{ labels: ['Apparel'] }], rows: [{ labels: [''], total: true, cells: [{ text: '1' }] }] } as RenderedWidget
    const client = fixtureClient({ ...bundle, results: { 'dashboard/board': { ...results, by_region: total } } })
    await show({ client })
    expect(host.querySelectorAll('[data-testid="vz-matrix-by_region"] th.vz-pointable').length).toBe(0)
  })
})

describe('drill', () => {
  it('a click on a row of a widget with a drill hands the host the target and the values of the row', async () => {
    const drills: ResolvedDrill[] = []
    const { client } = counting()
    // `top` passes `$params.region` as `year`: a list of nothing chosen, as the document's own value
    await show({ client, onDrill: (d) => drills.push(d) })
    await click(tableRow('East'))
    expect(drills).toEqual([{ kind: 'dashboard', name: 'detail', parameters: { region: 'East', year: [] } }])
  })

  it('Enter on a row drills too (rows take focus)', async () => {
    const drills: ResolvedDrill[] = []
    const { client } = counting()
    await show({ client, onDrill: (d) => drills.push(d) })
    const row = tableRow('West') as HTMLElement
    expect(row.tabIndex).toBe(0)
    await act(async () => { row.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true })) })
    expect(drills.map((d) => d.parameters.region)).toEqual(['West'])
  })

  it('a widget with both: a click selects, Alt+click drills', async () => {
    const drills: ResolvedDrill[] = []
    const { calls, client } = counting()
    await show({ client, onDrill: (d) => drills.push(d) })
    await click(rowLabel('West'))
    expect(drills).toEqual([])
    expect(calls[1]!.parameters).toEqual({ region: ['West'] })
    await click(rowLabel('East'), { altKey: true })
    expect(drills).toEqual([{ kind: 'dashboard', name: 'detail', parameters: { region: 'East' } }])
    expect(calls.length).toBe(2)
  })

  it('without a host that follows drills a table row does nothing and is not offered as clickable', async () => {
    const { calls, client } = counting()
    await show({ client })
    expect(host.querySelectorAll('[data-testid="vz-table-top"] tr.vz-pointable').length).toBe(0)
    await click(tableRow('West'))
    expect(calls.length).toBe(1)
  })

  it('a drill that passes a field the clicked row lacks is refused in the tile area, naming the field', async () => {
    const drills: ResolvedDrill[] = []
    const bad = text.replace('with: {region: $customers.region, year: $params.region}', 'with: {colour: $stores.colour}')
    const doc = validateText(bad).document as ViewsDoc
    const { client } = counting()
    await act(async () => root.render(<DashboardView name="board" document={doc} client={client} theme="light" onDrill={(d) => drills.push(d)} />))
    await click(tableRow('West'))
    expect(drills).toEqual([])
    expect(q('vz-interaction-notice')!.textContent).toContain('`stores.colour`')
    await click([...host.querySelectorAll('[data-testid="vz-interaction-notice"] button')][0]!)
    expect(q('vz-interaction-notice')).toBeNull()
  })
})

describe('refresh (fake clock, injected visibility)', () => {
  function source() {
    let hidden = false
    const listeners = new Set<() => void>()
    const visibility: Visibility = { hidden: () => hidden, subscribe: (l) => { listeners.add(l); return () => { listeners.delete(l) } } }
    return { visibility, set: async (value: boolean) => { hidden = value; await act(async () => { listeners.forEach((l) => l()) }) } }
  }
  const flush = async () => act(async () => { for (let i = 0; i < 10; i++) await Promise.resolve() })
  const advance = async (clock: ReturnType<typeof fakeClock>, ms: number) => { await act(async () => { clock.advance(ms) }); await flush() }
  // each resolution is read a minute later than the one before
  const stamped = (inner: ViewsClient): ViewsClient => {
    let n = 0
    return {
      ...inner,
      resolve: async (ref, layout, parameters, options) => {
        n++
        const r = await inner.resolve(ref, layout, parameters, options)
        return { ...r, as_of: `2026-11-08T09:${String(n).padStart(2, '0')}:00Z` }
      },
    }
  }

  it('re-resolves every placed widget at the interval, shows "as of hh:mm" per tile, and not before', async () => {
    const clock = fakeClock()
    const { calls, client: inner } = counting()
    const client = stamped(inner)
    const vis = source()
    await show({ client, clock, visibility: vis.visibility, random: () => 0, timeZone: 'UTC' })
    expect(calls.length).toBe(1)
    expect(q('vz-asof-net')!.textContent).toBe('as of 09:01')
    expect(q('vz-asof-top')!.textContent).toBe('as of 09:01')
    expect(q('vz-asof-note')).toBeNull()
    await advance(clock, 299_999)
    expect(calls.length).toBe(1)
    await advance(clock, 1)
    expect(calls.length).toBe(2)
    expect(calls[1]!.widgets).toEqual(['regions', 'net', 'note', 'by_region', 'top'])
    expect(q('vz-asof-net')!.textContent).toBe('as of 09:02')
    expect(q('vz-dashboard-host')!.getAttribute('data-refresh')).toBe('on')
    await advance(clock, 300_000)
    expect(calls.length).toBe(3)
  })

  it('does not refresh a hidden page, refreshes at once when it shows and the time has come', async () => {
    const clock = fakeClock()
    const { calls, client } = counting()
    const vis = source()
    await show({ client, clock, visibility: vis.visibility, random: () => 0 })
    await vis.set(true)
    expect(q('vz-dashboard-host')!.getAttribute('data-refresh')).toBe('paused')
    await advance(clock, 3_600_000)
    expect(calls.length).toBe(1)
    await vis.set(false)
    await flush()
    expect(calls.length).toBe(2)
    expect(q('vz-dashboard-host')!.getAttribute('data-refresh')).toBe('on')
  })

  it('a failed refresh keeps the rows shown and marks them stale; the next success clears the mark', async () => {
    const clock = fakeClock()
    const { client: inner } = counting()
    let failing = false
    let asked = 0
    const client: ViewsClient = { ...inner, resolve: async (...args) => { asked++; if (failing && asked > 1) throw new Error('the source is down'); return inner.resolve(...args) } }
    const vis = source()
    await show({ client, clock, visibility: vis.visibility, random: () => 0, timeZone: 'UTC' })
    failing = true
    await advance(clock, 300_000)
    expect(q('vz-dashboard-host')!.getAttribute('data-refresh')).toBe('stale')
    expect(q('vz-asof-net')!.textContent).toBe('as of 09:00, stale')
    expect(q('vz-metric')!.textContent).toContain('$3.30M')
    expect(q('vz-table-top')).not.toBeNull()
    expect(host.querySelectorAll('[role="alert"]').length).toBe(0)
    failing = false
    await advance(clock, 300_000)
    expect(q('vz-asof-net')!.textContent).toBe('as of 09:00')
    expect(q('vz-dashboard-host')!.getAttribute('data-refresh')).toBe('on')
  })

  it('a document without `refresh` has no timer and no freshness line, and a selection keeps the interval running with the new values', async () => {
    const clock = fakeClock()
    const none = validateText(text.replace('refresh: {every: 5m}\n', '')).document as ViewsDoc
    const { calls, client } = counting()
    await act(async () => root.render(<DashboardView name="board" document={none} client={client} theme="light" clock={clock} visibility={source().visibility} />))
    expect(clock.pending).toBe(0)
    expect(q('vz-asof-net')).toBeNull()
    await act(async () => root.render(<DashboardView name="board" document={document_} client={client} theme="light" clock={clock} visibility={source().visibility} random={() => 0} />))
    await click(rowLabel('West'))
    const before = calls.length
    await advance(clock, 300_000)
    expect(calls.length).toBe(before + 1)
    expect(calls[calls.length - 1]!.parameters).toEqual({ region: ['West'] })
  })

  it('leaving the page stops the timer', async () => {
    const clock = fakeClock()
    const { client } = counting()
    await show({ client, clock, visibility: source().visibility })
    expect(clock.pending).toBe(1)
    await act(async () => root.unmount())
    expect(clock.pending).toBe(0)
    root = createRoot(host)
  })
})

describe('kiosk', () => {
  it('shows the document\'s kiosk layout whatever the window selects, unless a layout is named; no maximize action', async () => {
    const { client } = counting()
    await show({ client })
    expect(layoutNow()).toBe('desk')
    expect(host.querySelector('button[aria-label^="Maximize"]')).not.toBeNull()
    await show({ client, kiosk: true })
    expect(layoutNow()).toBe('wall')
    expect(q('vz-dashboard')!.getAttribute('data-mode')).toBe('canvas')
    expect(q('vz-dashboard-host')!.hasAttribute('data-kiosk')).toBe(true)
    expect(host.querySelector('button[aria-label^="Maximize"]')).toBeNull()
    await show({ client, kiosk: true, layout: 'desk' })
    expect(layoutNow()).toBe('desk')
    expect(host.querySelector('button[aria-label^="Maximize"]')).toBeNull()
  })

  it('a kiosk wall fills the window as one scaled picture, refreshes, and cross-filters', async () => {
    const clock = fakeClock()
    const { calls, client } = counting()
    size = { width: 1280, height: 720 }
    await show({ client, kiosk: true, clock, visibility: { hidden: () => false, subscribe: () => () => {} }, random: () => 0 })
    expect(q('vz-dashboard')!.getAttribute('data-scale')).toBe('0.6667')
    await click(rowLabel('West'))
    expect(calls[1]!.layout).toBe('wall')
    await act(async () => { clock.advance(300_000) })
    await act(async () => { for (let i = 0; i < 10; i++) await Promise.resolve() })
    expect(calls.length).toBe(3)
    await resize(1280, 720)
    expect(layoutNow()).toBe('wall')
  })
})
