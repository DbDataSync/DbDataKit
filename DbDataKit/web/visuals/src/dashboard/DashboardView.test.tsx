// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureClient } from '../client/fixtureClient'
import type { ViewsClient } from '../client/types'
import { validateText } from '../client/text'
import { sampleBundle } from '../testing/sampleBundle'
import type { Box, ParameterValues, ViewsDoc } from '../views/model'
import { DashboardView, type DashboardViewProps } from './DashboardView'

// React wants to know it is in a test: updates must be inside `act`
;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const bundle = sampleBundle()
const document_ = validateText(bundle.documents['dashboard/retail-sales-today']!).document as ViewsDoc

/** A ResizeObserver the test drives: no timers, no layout engine; `resize` is what the browser would report. */
class FakeObserver {
  static all = new Set<FakeObserver>()
  disconnected = false
  readonly callback: (entries: Array<{ contentRect: { width: number; height: number } }>) => void
  constructor(callback: (entries: Array<{ contentRect: { width: number; height: number } }>) => void) {
    this.callback = callback
    FakeObserver.all.add(this)
  }
  observe() {}
  unobserve() {}
  disconnect() { this.disconnected = true; FakeObserver.all.delete(this) }
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
  host = document.createElement('div')
  document.body.appendChild(host)
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
  await act(async () => root.render(<DashboardView name="retail-sales-today" document={document_} theme="light" {...props} />))
}
const q = (id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLElement | null
const layoutNow = () => q('vz-dashboard')?.getAttribute('data-layout')

describe('DashboardView', () => {
  it('fills its container, picks the layout for it, resolves the layout\'s widgets through the client and draws them', async () => {
    const { calls, client } = counting()
    await show({ client })
    expect(layoutNow()).toBe('desk')
    expect(q('vz-dashboard')!.getAttribute('data-mode')).toBe('grid')
    expect(q('vz-dashboard')!.style.width).toBe('1366px')
    expect(calls).toEqual([{ layout: 'desk', widgets: ['regions', 'net', 'margin', 'note', 'by_region', 'top_products'], parameters: { region: [] } }])
    expect(q('vz-metric')!.textContent).toContain('$3.30M')
    expect(q('vz-table-top_products')).not.toBeNull()
    expect(host.querySelectorAll('[role="status"]').length).toBe(0)
    expect(host.querySelectorAll('[role="alert"]').length).toBe(0)
    expect(q('vz-fit-notice')!.textContent).toContain('12 of 24 rows')
  })

  it('draws at the box it is given, and measures nothing then', async () => {
    const { client } = counting()
    await show({ client, box: { width: 1920, height: 1080 } })
    expect(q('vz-dashboard')!.style.width).toBe('1920px')
    expect(FakeObserver.all.size).toBe(0)
    await show({ client, box: { width: 1280, height: 720 } })
    expect(q('vz-dashboard')!.style.width).toBe('1280px')
  })

  it('follows the container: a resize lays the tiles out again, and across a selector it switches layout', async () => {
    const { calls, client } = counting()
    await show({ client })
    expect(FakeObserver.all.size).toBe(1)
    await resize(1024, 768)
    expect(q('vz-dashboard')!.style.width).toBe('1024px')
    expect(layoutNow()).toBe('desk')
    await resize(390, 844)
    expect(layoutNow()).toBe('phone')
    expect(q('vz-engine-missing')).not.toBeNull()
    await resize(1366, 768)
    expect(layoutNow()).toBe('desk')
    // desk, phone and desk again place widgets that were all resolved at the first: nothing is asked twice
    expect(calls.length).toBe(1)
  })

  it('switching to a layout that places a widget not resolved yet asks only for that widget, with the same parameters', async () => {
    const { calls, client } = counting()
    size = { width: 390, height: 844 }
    await show({ client })
    expect(layoutNow()).toBe('phone')
    expect(calls).toEqual([{ layout: 'phone', widgets: ['regions', 'net', 'margin', 'by_region', 'top_products'], parameters: { region: [] } }])
    await resize(1366, 768)
    expect(layoutNow()).toBe('desk')
    expect(calls[1]).toEqual({ layout: 'desk', widgets: ['note'], parameters: { region: [] } })
    expect(q('vz-text')!.textContent).toContain('Showing')
    expect(q('vz-metric')!.textContent).toContain('$3.30M')
  })

  it('reports the layout it chose and how: by its selector, by name, by default', async () => {
    const chosen: string[] = []
    const { client } = counting()
    await show({ client, onSelect: (s) => chosen.push(`${s.layout.name}:${s.choice}`) })
    await resize(390, 844)
    await show({ client, layout: 'print', onSelect: (s) => chosen.push(`${s.layout.name}:${s.choice}`) })
    expect(chosen[0]).toBe('desk:default')
    expect(chosen).toContain('phone:matched')
  })

  it('a named layout wins over the selector, and a name the document lacks is refused naming the ones it has', async () => {
    const { client } = counting()
    await show({ client, layout: 'print' })
    expect(layoutNow()).toBe('print')
    expect(q('vz-engine-missing')!.textContent).toContain('flow layout')
    await show({ client, layout: 'tv' })
    expect(host.querySelector('[role="alert"]')!.textContent).toContain('no layout named `tv`')
    expect(host.querySelector('[role="alert"]')!.textContent).toContain('`desk`, `wall`, `phone`, `print`')
    expect(q('vz-dashboard')).toBeNull()
  })

  it('resolves only the widgets that use a changed parameter, and the control shows the value', async () => {
    const { calls, client } = counting()
    await show({ client, parameters: { region: [] } })
    await show({ client, parameters: { region: ['West'] } })
    expect(calls.map((c) => c.parameters)).toEqual([{ region: [] }, { region: ['West'] }])
    expect(calls[1]!.widgets).toEqual(['net', 'margin', 'note', 'top_products'])
    expect(q('vz-text')!.textContent).toContain('Showing West')
    const west = [...host.querySelectorAll('input[type="checkbox"]')].find((i) => i.parentElement!.textContent!.includes('West')) as HTMLInputElement
    expect(west.checked).toBe(true)
  })

  it('keeps its own parameters when none are given: a control changes them and the widgets resolve again', async () => {
    const changed: Array<[string, unknown]> = []
    const { calls, client } = counting()
    await show({ client, onParameterChange: (n, v) => changed.push([n, v]) })
    const east = [...host.querySelectorAll('input[type="checkbox"]')].find((i) => i.parentElement!.textContent!.includes('East')) as HTMLInputElement
    await act(async () => east.click())
    expect(changed).toEqual([['region', ['East']]])
    expect(calls.map((c) => c.parameters)).toEqual([{ region: [] }, { region: ['East'] }])
    expect(q('vz-text')!.textContent).toContain('Showing East')
  })

  it('maximizes a tile to the whole container and closes it again', async () => {
    const { client } = counting()
    await show({ client })
    const open = host.querySelector('button[aria-label="Maximize Best-selling products"]') as HTMLButtonElement
    await act(async () => open.click())
    expect(q('vz-dashboard')!.getAttribute('data-mode')).toBe('maximized')
    expect(q('vz-table-top_products')!.querySelectorAll('tbody tr').length).toBe(24)
    expect(q('vz-widget-net')).toBeNull()
    const close = host.querySelector('button[aria-label="Close the full view of Best-selling products"]') as HTMLButtonElement
    await act(async () => close.click())
    expect(q('vz-dashboard')!.getAttribute('data-mode')).toBe('grid')
    expect(q('vz-widget-net')).not.toBeNull()
  })

  it('a widget with no recorded result shows its own error and the others draw', async () => {
    const thin = structuredClone(bundle)
    delete thin.results!['dashboard/retail-sales-today']!.by_region
    await show({ client: fixtureClient(thin) })
    const alerts = [...host.querySelectorAll('[role="alert"]')]
    expect(alerts.length).toBe(1)
    expect(alerts[0]!.textContent).toContain('no_recorded_result')
    expect(q('vz-metric')!.textContent).toContain('$3.30M')
  })

  it('a failure of the call itself is shown once, for the page, and the tiles wait', async () => {
    const { client } = counting({ resolve: async () => { throw new Error('The views module could not be reached: connection refused') } })
    await show({ client })
    const alerts = [...host.querySelectorAll('[role="alert"]')]
    expect(alerts.length).toBe(1)
    expect(alerts[0]!.textContent).toContain('connection refused')
    expect(host.querySelectorAll('[role="status"]').length).toBeGreaterThan(0)
  })

  it('draws the tiles as loading until the client answers', async () => {
    let release!: () => void
    const gate = new Promise<void>((r) => { release = r })
    const inner = fixtureClient(bundle)
    const client: ViewsClient = { ...inner, resolve: async (...a) => { await gate; return inner.resolve(...a) } }
    await show({ client })
    expect(host.querySelectorAll('[role="status"]').length).toBe(4)
    expect(q('vz-text')).not.toBeNull()
    await act(async () => release())
    expect(host.querySelectorAll('[role="status"]').length).toBe(0)
  })

  it('ignores an answer that arrives after the layout moved on, and stops observing when it goes', async () => {
    const releases: Array<() => void> = []
    const inner = fixtureClient(bundle)
    const client: ViewsClient = { ...inner, resolve: (...a) => new Promise((resolve) => { releases.push(() => resolve(inner.resolve(...a))) }) }
    await show({ client })
    await resize(390, 844)
    expect(releases.length).toBe(2)
    await act(async () => releases[0]!())
    expect(q('vz-metric')).toBeNull()
    await act(async () => releases[1]!())
    expect(layoutNow()).toBe('phone')
    const observer = [...FakeObserver.all][0]!
    await act(async () => root.unmount())
    expect(observer.disconnected).toBe(true)
    root = createRoot(host)
  })
})
