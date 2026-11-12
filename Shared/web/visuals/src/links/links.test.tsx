// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureClient, type FixtureBundle } from '../client/fixtureClient'
import { validateText } from '../client/text'
import { DashboardView, type DashboardViewProps, type LinkEvent } from '../dashboard/DashboardView'
import type { Box, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const text = `
schemaVersion: 1
kind: dashboard
title: Board
links:
  edit-order: {form: orders, edit: {region: $customers.region, units: $sales.units}, as: dialog, then: refresh, lock: [region], label: Edit order}
  quiet: {form: orders, new: {region: $customers.region}, as: dialog, then: none}
  side: {page: detail, with: {region: $customers.region}, as: panel}
  go: {page: detail, with: {region: $customers.region}}
  also: {form: orders, new: {region: $customers.region}, as: dialog, then: [totals]}
widgets:
  orders:
    type: table
    title: Orders
    encodings: {columns: [{field: customers.region, label: Region}, {field: sales.units, label: Units}]}
    query: {model: retail_sales}
    on_click: edit-order
  other:
    type: table
    title: Others
    encodings: {columns: [{field: customers.region, label: Region}, {field: sales.units, label: Units}]}
    query: {model: retail_sales}
    on_click: side
  totals: {type: metric, label: Total, encodings: {value: sales.units}, query: {model: retail_sales}}
  grid: {type: custom, title: Grid, component: sales-portal/Grid, links: [edit-order]}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 8}
    tiles:
      - {widget: orders, x: 0, y: 0, w: 6, h: 4}
      - {widget: other, x: 6, y: 0, w: 6, h: 4}
      - {widget: totals, x: 0, y: 4, w: 4, h: 2}
      - {widget: grid, x: 4, y: 4, w: 8, h: 4}
`
const document_ = validateText(text).document as ViewsDoc
const table: RenderedWidget = { type: 'table', columns: [{ field: 'customers.region', label: 'Region' }, { field: 'sales.units', label: 'Units' }], rows: [{ cells: [{ v: 'West', text: 'West' }, { v: 90, text: '90' }] }, { cells: [{ v: 'East', text: 'East' }, { v: 70, text: '70' }] }] }
const results: Record<string, RenderedWidget> = {
  orders: table, other: table, totals: { type: 'metric', value: { v: 160, text: '160' } },
  grid: { type: 'custom', component: 'sales-portal/Grid', data: {} },
}
const bundle: FixtureBundle = { documents: { 'dashboard/board': text }, results: { 'dashboard/board': results } }

class FakeObserver { observe() {} unobserve() {} disconnect() {} }

describe('the shell opens a link target in a dialog or a side panel (DBR36)', () => {
  let host: HTMLDivElement
  let root: Root
  beforeEach(() => {
    ;(globalThis as { ResizeObserver?: unknown }).ResizeObserver = FakeObserver
    host = window.document.createElement('div')
    window.document.body.appendChild(host)
    root = createRoot(host)
  })
  afterEach(async () => { await act(async () => root.unmount()); host.remove() })

  const box: Box = { width: 1200, height: 700 }
  const calls: string[][] = []
  let current = fixtureClient(bundle)
  beforeEach(() => { calls.length = 0; current = fixtureClient(bundle, { onResolve: (c) => calls.push([...c.widgets]) }) })
  const linkHost = { route: (link: { targetKind: string; target: string }) => (link.target === 'orders' ? '/apps/sales/forms/orders' : `/apps/sales/${link.targetKind}s/${link.target}`), navigate: (address: string) => { navigated.push(address) } }
  const navigated: string[] = []
  const show = (props: Partial<DashboardViewProps> = {}) => act(async () => root.render(<DashboardView name="board" document={document_} theme="light" box={box} client={current} linkHost={linkHost} customViews={{ frameSrc: () => '/apps/sales/_views/frame.html' }} {...props} />))
  const q = (id: string) => host.ownerDocument.querySelector(`[data-testid="${id}"]`) as HTMLElement | null
  const row = (table: string, name: string) => [...host.querySelectorAll(`[data-testid="vz-table-${table}"] tbody tr`)].find((e) => e.textContent!.startsWith(name))!
  const click = (el: Element) => act(async () => { el.dispatchEvent(new window.MouseEvent('click', { bubbles: true })) })
  const frameWindow = (id: string) => {
    const frame = q(id) as HTMLIFrameElement
    const fake = { postMessage: () => {} }
    Object.defineProperty(frame, 'contentWindow', { configurable: true, get: () => fake })
    return { say: (data: unknown, source: unknown = fake) => act(async () => { window.dispatchEvent(new window.MessageEvent('message', { data, source: source as Window })) }) }
  }

  it('a click on a row opens a dialog with the target prefilled from the row and the locked names, and the page behind it is inert', async () => {
    await show()
    expect(q('vz-surface-dialog')).toBeNull()
    await click(row('orders', 'East'))
    const dialog = q('vz-surface-dialog')!
    expect(dialog).not.toBeNull()
    expect(dialog.querySelector('[role="dialog"]')?.getAttribute('aria-modal')).toBe('true')
    expect(dialog.querySelector('[role="dialog"]')?.getAttribute('aria-label')).toBe('Edit order')
    const frame = q('vz-surface-frame') as HTMLIFrameElement
    expect(frame.getAttribute('src')).toBe('/apps/sales/forms/orders?region=East&units=70&_lock=region')
    expect(frame.getAttribute('sandbox')).toBe('allow-scripts allow-forms')
    expect(q('vz-dashboard-host')!.hasAttribute('inert')).toBe(true)
    expect(host.ownerDocument.activeElement).toBe(q('vz-surface-close'))
  })

  it('saving closes it and the source widget alone resolves again; the page is usable again and focus returns', async () => {
    await show()
    const opener = row('orders', 'West') as HTMLElement
    opener.setAttribute('tabindex', '0')
    opener.focus()
    await click(opener)
    expect(calls).toEqual([['orders', 'other', 'totals', 'grid']])
    const { say } = frameWindow('vz-surface-frame')
    await say({ type: 'views:saved' }, {})                       // not the surface's window: ignored
    expect(q('vz-surface-dialog')).not.toBeNull()
    await say({ type: 'views:saved' })
    expect(q('vz-surface-dialog')).toBeNull()
    expect(calls).toEqual([['orders', 'other', 'totals', 'grid'], ['orders']])
    expect(q('vz-dashboard-host')!.hasAttribute('inert')).toBe(false)
    expect(host.ownerDocument.activeElement).toBe(opener)
  })

  it('then: none and a close without a save resolve nothing; a list of widgets resolves those', async () => {
    await show()
    await show({ document: { ...document_, widgets: { ...document_.widgets, orders: { ...document_.widgets.orders!, on_click: 'quiet' } as never } } })
    await click(row('orders', 'West'))
    await frameWindow('vz-surface-frame').say({ type: 'views:saved' })
    expect(calls.length).toBe(1)                                // then: none
    await show({ document: { ...document_, widgets: { ...document_.widgets, orders: { ...document_.widgets.orders!, on_click: 'also' } as never } } })
    await click(row('orders', 'West'))
    await frameWindow('vz-surface-frame').say({ type: 'views:saved' })
    expect(calls.at(-1)).toEqual(['totals'])                    // then: [totals]
    const before = calls.length
    await click(row('orders', 'West'))
    await click(q('vz-surface-close')!)
    expect(q('vz-surface-dialog')).toBeNull()
    expect(calls.length).toBe(before)                           // closed without a save
  })

  it('Escape closes it', async () => {
    await show()
    await click(row('orders', 'West'))
    await act(async () => { q('vz-surface-close')!.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    expect(q('vz-surface-dialog')).toBeNull()
  })

  it('a panel leaves the page usable; a navigating link goes through the host; a target with no address says so', async () => {
    await show()
    await click(row('other', 'East'))
    expect(q('vz-surface-panel')).not.toBeNull()
    expect(q('vz-surface-panel')!.querySelector('[role="complementary"]')).not.toBeNull()
    expect((q('vz-surface-frame') as HTMLIFrameElement).getAttribute('src')).toBe('/apps/sales/pages/detail?region=East')
    expect(q('vz-dashboard-host')!.hasAttribute('inert')).toBe(false)
    await click(q('vz-surface-close')!)

    await show({ document: { ...document_, widgets: { ...document_.widgets, other: { ...document_.widgets.other!, on_click: 'go' } as never } } })
    await click(row('other', 'East'))
    expect(navigated).toEqual(['/apps/sales/pages/detail?region=East'])
    expect(q('vz-surface-panel')).toBeNull()

    await show({ linkHost: { route: () => null } })
    await click(row('orders', 'East'))
    expect(q('vz-interaction-notice')?.textContent).toContain('has no address for')
  })

  it('without a link host or a handler a widget with an on_click is not pointable', async () => {
    await show({ linkHost: undefined })
    await click(row('orders', 'East'))
    expect(q('vz-surface-dialog')).toBeNull()
  })

  it('a handler of the host takes the event instead of the surface', async () => {
    const events: LinkEvent[] = []
    await show({ onLink: (e) => events.push(e) })
    await click(row('orders', 'East'))
    expect(q('vz-surface-dialog')).toBeNull()
    expect(events[0]).toMatchObject({ widget: 'orders', values: { region: 'East', units: 70 }, address: '/apps/sales/forms/orders?region=East&units=70&_lock=region' })
  })

  it('a custom widget opens the same dialog, is told when it closes, and its source resolves again', async () => {
    await show()
    const grid = frameWindow('vz-custom-frame-grid')
    await grid.say({ type: 'views:hello' })
    await grid.say({ type: 'views:open', link: 'edit-order', row: { 'customers.region': 'West', 'sales.units': 5 } })
    expect((q('vz-surface-frame') as HTMLIFrameElement).getAttribute('src')).toBe('/apps/sales/forms/orders?region=West&units=5&_lock=region')
    await frameWindow('vz-surface-frame').say({ type: 'views:saved' })
    expect(q('vz-surface-dialog')).toBeNull()
    expect(calls.at(-1)).toEqual(['grid'])
  })
})
