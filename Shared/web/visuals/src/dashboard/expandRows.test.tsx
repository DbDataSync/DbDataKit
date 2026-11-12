// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureClient, type FixtureBundle } from '../client/fixtureClient'
import { validateText } from '../client/text'
import type { ResolveOptions, ViewsClient } from '../client/types'
import { MatrixGrid } from '../MatrixGrid'
import type { RenderedMatrix } from '../types'
import type { ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { DashboardView } from './DashboardView'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

/** Drill detail groups (plan 32 DBR55): a row that can expand has a toggle with `aria-expanded`, its details are `children` under it, and a click runs only that widget again with `expand`. */
const cell = (v: number) => ({ v, text: String(v) })
const collapsed: RenderedMatrix = {
  id: 'orders', type: 'matrix', columns: [{ labels: ['Jan'] }, { labels: ['Feb'] }],
  rows: [
    { labels: ['West'], expandable: true, key: ['West'], cells: [cell(5), cell(6)] },
    { labels: ['East'], expandable: true, key: ['East'], cells: [cell(7), cell(8)] },
    { labels: ['Total'], total: true, cells: [cell(12), cell(14)] },
  ],
}
const opened = (...regions: string[]): RenderedMatrix => ({
  ...collapsed,
  rows: collapsed.rows.map((r) => (regions.includes(r.labels[0]!) ? { ...r, expanded: true, children: [{ labels: ['Ada', 'Open'], cells: [cell(2), cell(3)] }, { labels: ['Bo', 'Closed'], cells: [cell(3), cell(3)] }] } : r)),
})

describe('the matrix grid with rows that expand', () => {
  it('draws a toggle with aria-expanded where a handler is given, and none without', () => {
    const out = renderToStaticMarkup(<MatrixGrid view={collapsed} onExpand={() => {}} />)
    expect(out.match(/aria-expanded="false"/g)).toHaveLength(4)       // the row and its button, twice (the total row has none)
    expect(out).toContain('data-testid="vz-expand-orders-0"')
    expect(out).not.toContain('vz-expand-orders-2')
    expect(renderToStaticMarkup(<MatrixGrid view={collapsed} />)).not.toContain('<button')
  })
  it('draws the children of an expanded row under it, with the detail labels after the row\'s own', () => {
    const out = renderToStaticMarkup(<MatrixGrid view={opened('West')} onExpand={() => {}} />)
    expect(out).toContain('aria-expanded="true"')
    expect(out).toContain('data-testid="vz-child-orders-0-0"')
    expect(out).toContain('data-testid="vz-child-orders-0-1"')
    expect(out).toContain('Ada')
    expect(out).toContain('Closed')
    expect(out).toContain('Collapse West')
    // a collapsed row keeps its children away even when it carries them
    expect(renderToStaticMarkup(<MatrixGrid view={{ ...opened('West'), rows: opened('West').rows.map((r) => ({ ...r, expanded: false })) }} onExpand={() => {}} />)).not.toContain('vz-child-')
  })
})

const text = `
schemaVersion: 1
kind: dashboard
title: Board
groups:
  who: {fields: [customers.customer, orders.status]}
widgets:
  orders:
    type: matrix
    title: Orders
    encodings:
      rows: [customers.region, {group: who, show: drill}]
      columns: [date.month]
      values: [{field: sales.net_amount}]
    query: {model: retail_sales}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 8}
    tiles:
      - {widget: orders, x: 0, y: 0, w: 12, h: 8}
`
const document_ = validateText(text).document as ViewsDoc
const bundle: FixtureBundle = { documents: { 'dashboard/board': text }, results: { 'dashboard/board': { orders: collapsed as unknown as RenderedWidget } } }

let host: HTMLDivElement
let root: Root
beforeEach(() => {
  class Observer { cb: (e: Array<{ contentRect: { width: number; height: number } }>) => void; constructor(cb: (e: Array<{ contentRect: { width: number; height: number } }>) => void) { this.cb = cb } observe() { this.cb([{ contentRect: { width: 1200, height: 700 } }]) } unobserve() {} disconnect() {} }
  ;(globalThis as { ResizeObserver?: unknown }).ResizeObserver = Observer
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get: () => 1200 })
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, get: () => 700 })
  host = window.document.createElement('div')
  window.document.body.appendChild(host)
  root = createRoot(host)
})
afterEach(async () => {
  await act(async () => root.unmount())
  host.remove()
})

function client(expands: boolean) {
  const asked: Array<ResolveOptions | undefined> = []
  const base = fixtureClient(bundle)
  const c: ViewsClient = {
    ...base,
    ...(expands ? { expandsOnServer: true } : {}),
    async resolve(ref, layout, parameters, options) {
      asked.push(options)
      const answer = await base.resolve(ref, layout, parameters, options)
      const keys = options?.expand?.orders
      return keys ? { ...answer, widgets: { ...answer.widgets, orders: opened(...keys.map((k) => String(k[0]))) as unknown as RenderedWidget } } : answer
    },
  }
  return { asked, client: c }
}
const q = (id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLElement | null
const click = async (el: Element) => act(async () => { el.dispatchEvent(new window.MouseEvent('click', { bubbles: true })) })

describe('expanding a row of a board', () => {
  it('a click runs only that widget again with the row\'s key as `expand`, shows its details, and a second row keeps its own; collapsing takes one away', async () => {
    const { asked, client: c } = client(true)
    await act(async () => root.render(<DashboardView name="board" document={document_} client={c} theme="light" />))
    expect(q('vz-expand-orders-0')?.getAttribute('aria-expanded')).toBe('false')
    expect(asked).toHaveLength(1)

    await click(q('vz-expand-orders-0')!)
    expect(asked).toHaveLength(2)
    expect(asked[1]).toMatchObject({ only: ['orders'], expand: { orders: [['West']] } })
    expect(q('vz-expand-orders-0')?.getAttribute('aria-expanded')).toBe('true')
    expect(q('vz-child-orders-0-0')?.textContent).toContain('Ada')

    await click(q('vz-expand-orders-1')!)
    expect(asked[2]).toMatchObject({ expand: { orders: [['West'], ['East']] } })
    expect(q('vz-child-orders-0-1')).not.toBeNull()
    expect(q('vz-child-orders-1-0')).not.toBeNull()          // two rows keep their own details

    await click(q('vz-expand-orders-0')!)
    expect(asked[3]).toMatchObject({ expand: { orders: [['East']] } })
    expect(q('vz-child-orders-0-0')).toBeNull()
    expect(q('vz-child-orders-1-0')).not.toBeNull()          // East stays open
  })

  it('draws no toggle when the source cannot read details', async () => {
    const { client: c } = client(false)
    await act(async () => root.render(<DashboardView name="board" document={document_} client={c} theme="light" />))
    expect(q('vz-expand-orders-0')).toBeNull()
    expect(q('vz-matrix-orders')).not.toBeNull()
  })
})
