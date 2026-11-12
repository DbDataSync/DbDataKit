// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureClient, type FixtureBundle } from '../client/fixtureClient'
import { validateText } from '../client/text'
import { DashboardView, type DashboardViewProps } from '../dashboard/DashboardView'
import type { Box, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import type { ActionHost, ActionOutcome, ActionRequest } from './actions'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const text = `
schemaVersion: 1
kind: dashboard
title: Board
links:
  close-order:
    change: {model: sales, entity: orders, edit: {key: $orders.id, version: $orders.version}, set: {status: Closed}}
    confirm: {title: "Close order {{orders.number}}", show: [orders.customer], button: Close order}
  drop-order:
    change: {model: sales, entity: orders, delete: {key: $orders.id}}
    confirm: {title: "Delete order {{orders.number}}"}
  set-status:
    change: {model: sales, entity: orders, edit: {key: $orders.id}, set: {status: $ask.status}}
    ask: {status: {label: New status, choices: [Open, Held]}}
    confirm: {title: "Set {{orders.number}} to {{ask.status}}"}
  rerun:
    task: {package: nightly, task: load, inputs: {number: $orders.number}}
widgets:
  orders:
    type: table
    title: Orders
    encodings: {columns: [{field: orders.id, label: Id}, {field: orders.version, label: Version}, {field: orders.number, label: Number}, {field: orders.customer, label: Customer}]}
    query: {model: sales}
    on_click: close-order
  totals: {type: metric, label: Total, encodings: {value: sales.units}, query: {model: sales}}
  grid: {type: custom, title: Grid, component: sales-portal/Grid, links: [close-order]}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 8}
    tiles:
      - {widget: orders, x: 0, y: 0, w: 8, h: 4}
      - {widget: totals, x: 8, y: 0, w: 4, h: 2}
      - {widget: grid, x: 0, y: 4, w: 12, h: 4}
`
const document_ = validateText(text).document as ViewsDoc
const cell = (v: string | number) => ({ v, text: String(v) })
const table: RenderedWidget = {
  type: 'table',
  columns: [{ field: 'orders.id', label: 'Id' }, { field: 'orders.version', label: 'Version' }, { field: 'orders.number', label: 'Number' }, { field: 'orders.customer', label: 'Customer' }],
  rows: [{ cells: [cell(42), cell(7), cell('SO-42'), cell('Acme')] }, { cells: [cell(43), cell(2), cell('SO-43'), cell('<b>Initech</b>')] }],
}
const results: Record<string, RenderedWidget> = { orders: table, totals: { type: 'metric', value: { v: 1, text: '1' } }, grid: { type: 'custom', component: 'sales-portal/Grid', data: {} } }
const bundle: FixtureBundle = { documents: { 'dashboard/board': text }, results: { 'dashboard/board': results } }

class FakeObserver { observe() {} unobserve() {} disconnect() {} }

describe('the shell confirms an action in the declaration\'s words (DBR53)', () => {
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
  const requests: ActionRequest[] = []
  let outcome: ActionOutcome = { status: 'applied' }
  let release: (() => void) | null = null
  let hold = false
  const actionHost: ActionHost = {
    run: async (r) => { requests.push(r); if (hold) await new Promise<void>((ok) => { release = ok }); return outcome },
    forecast: async (link) => (link === 'close-order' ? { will: 'wait', note: 'This will wait for approval by the sales owner.' } : { will: 'apply' }),
  }
  let current = fixtureClient(bundle)
  beforeEach(() => { calls.length = 0; requests.length = 0; outcome = { status: 'applied' }; hold = false; release = null; current = fixtureClient(bundle, { onResolve: (c) => calls.push([...c.widgets]) }) })
  const show = (props: Partial<DashboardViewProps> = {}) => act(async () => root.render(<DashboardView name="board" document={document_} theme="light" box={box} client={current} actionHost={actionHost} customViews={{ frameSrc: () => '/apps/sales/_views/frame.html' }} {...props} />))
  const q = (id: string) => host.ownerDocument.querySelector(`[data-testid="${id}"]`) as HTMLElement | null
  const row = (name: string) => [...host.querySelectorAll('[data-testid="vz-table-orders"] tbody tr')].find((e) => e.textContent!.includes(name))!
  const click = (el: Element) => act(async () => { el.dispatchEvent(new window.MouseEvent('click', { bubbles: true })) })
  const withLink = (widget: string, link: string) => ({ document: { ...document_, widgets: { ...document_.widgets, [widget]: { ...document_.widgets[widget]!, on_click: link } as never } } })
  const type = (el: HTMLElement, value: string) => act(async () => {
    const proto = el.tagName === 'SELECT' ? window.HTMLSelectElement.prototype : window.HTMLInputElement.prototype
    Object.getOwnPropertyDescriptor(proto, 'value')!.set!.call(el, value)
    el.dispatchEvent(new window.Event(el.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true }))
  })

  it('a click on a row opens a popup that says what will change: the title and shown fields from the declaration, the values verbatim, what will happen', async () => {
    await show()
    expect(q('vz-action')).toBeNull()
    await click(row('SO-42'))
    expect(q('vz-action-title')!.textContent).toBe('Close order SO-42')
    expect(q('vz-action')!.querySelector('[role="dialog"]')?.getAttribute('aria-modal')).toBe('true')
    expect(q('vz-action-shows')!.textContent).toContain('Acme')
    expect(q('vz-action-writes')!.textContent).toContain('sales.orders')
    expect(q('vz-action-write-status')!.textContent).toBe('statusClosed')
    expect(q('vz-action-writes')!.textContent).toContain('row key42')
    expect(q('vz-action-writes')!.textContent).toContain('version7')
    expect(q('vz-action-forecast')!.textContent).toBe('This will wait for approval by the sales owner.')
    expect(q('vz-action-confirm')!.textContent).toBe('Close order')
    expect(q('vz-dashboard-host')!.hasAttribute('inert')).toBe(true)
    expect(requests).toEqual([])                                       // nothing is sent before the person confirms
  })

  it('values from the row are plain text, never markup', async () => {
    await show()
    await click(row('SO-43'))
    expect(q('vz-action-shows')!.textContent).toContain('<b>Initech</b>')
    expect(q('vz-action-shows')!.querySelector('b')).toBeNull()
  })

  it('confirming sends the link, the row and the idempotency key once, shows the outcome, and the source widget resolves again', async () => {
    await show()
    await click(row('SO-42'))
    await click(q('vz-action-confirm')!)
    expect(requests).toHaveLength(1)
    expect(requests[0]).toMatchObject({ link: 'close-order', widget: 'orders', row: { 'orders.id': 42, 'orders.version': 7, 'orders.number': 'SO-42' } })
    expect(requests[0]!.idempotencyKey.length).toBeGreaterThan(8)
    expect(q('vz-action-done')!.textContent).toBe('The change was made.')
    expect(calls.at(-1)).toEqual(['orders'])
    expect(q('vz-action-confirm')).toBeNull()
    await click(q('vz-action-cancel')!)
    expect(q('vz-action')).toBeNull()
    expect(q('vz-dashboard-host')!.hasAttribute('inert')).toBe(false)
  })

  it('a double click is one call, with one key', async () => {
    hold = true
    await show()
    await click(row('SO-42'))
    const confirm = q('vz-action-confirm')!
    await click(confirm)
    await click(confirm)
    await click(confirm)
    expect(requests).toHaveLength(1)
    expect((q('vz-action-confirm') as HTMLButtonElement).disabled).toBe(true)
    await act(async () => release?.())
    expect(requests).toHaveLength(1)
  })

  it('the Store\'s 202 is shown as waiting for approval', async () => {
    outcome = { status: 'waiting' }
    await show()
    await click(row('SO-42'))
    await click(q('vz-action-confirm')!)
    expect(q('vz-action-done')!.getAttribute('data-status')).toBe('waiting')
    expect(q('vz-action-done')!.textContent).toContain('Waiting for approval')
  })

  it('a stale row version is a conflict and a rule the Store broke is listed by field; the popup stays open to try again', async () => {
    outcome = { status: 'conflict', message: 'The row was changed by someone else (now version 8).' }
    await show()
    await click(row('SO-42'))
    await click(q('vz-action-confirm')!)
    expect(q('vz-action-failed')!.getAttribute('data-status')).toBe('conflict')
    expect(q('vz-action-failed')!.textContent).toContain('version 8')
    expect((q('vz-action-confirm') as HTMLButtonElement).disabled).toBe(false)
    outcome = { status: 'refused', message: 'The change breaks the model\'s rules.', violations: [{ rule: 'allowed', attribute: 'status', message: 'Closed is not allowed.' }] }
    await click(q('vz-action-confirm')!)
    expect(q('vz-action-failed')!.textContent).toContain('status: Closed is not allowed.')
    expect(requests).toHaveLength(2)
    expect(requests[1]!.idempotencyKey).toBe(requests[0]!.idempotencyKey)
    expect(calls).toHaveLength(1)                                      // nothing resolved again after a refusal
  })

  it('a delete is destructive: the popup starts on Cancel and Enter does not confirm it', async () => {
    await show(withLink('orders', 'drop-order'))
    await click(row('SO-42'))
    expect(q('vz-action-confirm')!.className).toContain('vz-button-danger')
    expect(q('vz-action-confirm')!.textContent).toBe('Delete the row')
    expect(host.ownerDocument.activeElement).toBe(q('vz-action-cancel'))
    await act(async () => { q('vz-action-title')!.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })) })
    expect(requests).toHaveLength(0)
  })

  it('a row that lacks what the link needs cannot be confirmed and the popup says what is missing', async () => {
    const noKey = { type: 'table', columns: [{ field: 'orders.number', label: 'Number' }], rows: [{ cells: [cell('SO-1')] }] } as RenderedWidget
    await show({ client: fixtureClient({ documents: bundle.documents, results: { 'dashboard/board': { ...results, orders: noKey } } }) })
    await click(row('SO-1'))
    expect(q('vz-action-missing')!.textContent).toContain('the row key')
    expect((q('vz-action-confirm') as HTMLButtonElement).disabled).toBe(true)
  })

  it('the fields the person fills are checked, listed in the title and sent as answers', async () => {
    await show(withLink('orders', 'set-status'))
    await click(row('SO-42'))
    expect(q('vz-action-title')!.textContent).toBe('Set SO-42 to ')
    await click(q('vz-action-confirm')!)
    expect(q('vz-action')!.textContent).toContain('New status is needed.')
    expect(requests).toHaveLength(0)
    await type(q('vz-action-ask-status')!, 'Held')
    expect(q('vz-action-title')!.textContent).toBe('Set SO-42 to Held')
    expect(q('vz-action-write-status')!.textContent).toBe('statusHeld')
    await click(q('vz-action-confirm')!)
    expect(requests[0]).toMatchObject({ link: 'set-status', answers: { status: 'Held' } })
  })

  it('a task is confirmed the same way', async () => {
    outcome = { status: 'started' }
    await show(withLink('orders', 'rerun'))
    await click(row('SO-42'))
    expect(q('vz-action-title')!.textContent).toBe('Run nightly.load')
    expect(q('vz-action-write-number')!.textContent).toBe('numberSO-42')
    await click(q('vz-action-confirm')!)
    expect(q('vz-action-done')!.getAttribute('data-status')).toBe('started')
  })

  it('a custom widget fires the same popup and cannot change its words', async () => {
    await show()
    const frame = q('vz-custom-frame-grid') as HTMLIFrameElement
    const fake = { postMessage: () => {} }
    Object.defineProperty(frame, 'contentWindow', { configurable: true, get: () => fake })
    const say = (data: unknown) => act(async () => { window.dispatchEvent(new window.MessageEvent('message', { data, source: fake as unknown as Window })) })
    await say({ type: 'views:hello' })
    await say({ type: 'views:open', link: 'close-order', row: { 'orders.id': 9, 'orders.version': 1, 'orders.number': 'SO-9', 'orders.customer': 'Globex' }, title: 'Harmless', button: 'Nothing', set: { status: 'Open' } })
    expect(q('vz-action-title')!.textContent).toBe('Close order SO-9')
    expect(q('vz-action-confirm')!.textContent).toBe('Close order')
    expect(q('vz-action-write-status')!.textContent).toBe('statusClosed')
    expect(requests).toHaveLength(0)
  })

  it('Escape cancels and nothing was sent; without a host that runs actions the click says so', async () => {
    await show()
    await click(row('SO-42'))
    await act(async () => { q('vz-action-cancel')!.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    expect(q('vz-action')).toBeNull()
    expect(requests).toHaveLength(0)
    await show({ actionHost: undefined, linkHost: { route: () => null } })
    await click(row('SO-42'))
    expect(q('vz-interaction-notice')?.textContent).toContain('cannot run actions')
  })
})
