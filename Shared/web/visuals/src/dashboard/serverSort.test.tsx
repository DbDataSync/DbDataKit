// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureClient, type FixtureBundle } from '../client/fixtureClient'
import { validateText } from '../client/text'
import type { ResolveOptions, ViewsClient } from '../client/types'
import type { ViewsDoc } from '../views/model'
import type { RenderedTable, RenderedWidget } from '../views/rendered'
import { DashboardView } from './DashboardView'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

// A board with one table the server cut (4 rows of 9): a click on a header runs the widget again with `order` (DBR45) when the client sorts on the server.
const text = `
schemaVersion: 1
kind: dashboard
title: Board
widgets:
  top:
    type: table
    title: Top products
    encodings: {columns: [{field: products.name, label: Product}, {field: sales.units, label: Units}]}
    query: {model: retail_sales}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 8}
    tiles:
      - {widget: top, x: 0, y: 0, w: 12, h: 8}
`
const document_ = validateText(text).document as ViewsDoc
const rows = (names: Array<[string, number]>): RenderedTable['rows'] => names.map(([n, u]) => ({ cells: [{ v: n, text: n }, { v: u, text: String(u) }] }))
const cut = (names: Array<[string, number]>): RenderedWidget => ({ type: 'table', columns: [{ field: 'products.name', label: 'Product' }, { field: 'sales.units', label: 'Units' }], rows: rows(names), total_rows: 9 })
const documentOrder = cut([['b', 10], ['a', 2], ['c', 7], ['d', 4]])
const byUnitsDescending = cut([['z', 99], ['y', 98], ['x', 97], ['w', 96]])
const bundle: FixtureBundle = { documents: { 'dashboard/board': text }, results: { 'dashboard/board': { top: documentOrder } } }

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

function serverClient(sorts: boolean) {
  const asked: Array<ResolveOptions | undefined> = []
  const base = fixtureClient(bundle)
  const client: ViewsClient = {
    ...base,
    ...(sorts ? { sortsOnServer: true } : {}),
    async resolve(ref, layout, parameters, options) {
      asked.push(options)
      const answer = await base.resolve(ref, layout, parameters, options)
      const order = options?.order?.top
      return order ? { ...answer, widgets: { ...answer.widgets, top: order.by === 'sales.units' && order.dir === 'desc' ? byUnitsDescending : documentOrder } } : answer
    },
  }
  return { asked, client }
}
const q = (id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLElement | null
const names = () => [...host.querySelectorAll('[data-testid="vz-table-top"] tbody tr td:first-child')].map((e) => e.textContent)
const click = async (el: Element) => act(async () => { el.dispatchEvent(new window.MouseEvent('click', { bubbles: true })) })

describe('click-to-sort on a table the server cut', () => {
  it('a click on a header runs only that widget again with `order`; a second click reverses; a third gives the document order back', async () => {
    const { asked, client } = serverClient(true)
    await act(async () => root.render(<DashboardView name="board" document={document_} client={client} theme="light" sortable />))
    expect(names()).toEqual(['b', 'a', 'c', 'd'])
    expect(asked).toHaveLength(1)
    // ascending first: the module answers the document order here, to show the rows are the server's and not sorted over the four loaded
    await click(q('vz-sort-top-1')!)
    expect(asked[1]).toMatchObject({ only: ['top'], order: { top: { by: 'sales.units', dir: 'asc' } } })
    expect(names()).toEqual(['b', 'a', 'c', 'd'])
    await click(q('vz-sort-top-1')!)
    expect(asked[2]).toMatchObject({ only: ['top'], order: { top: { by: 'sales.units', dir: 'desc' } } })
    expect(names()).toEqual(['z', 'y', 'x', 'w'])
    expect(host.querySelector('[data-testid="vz-table-top"] th:nth-child(2)')!.getAttribute('aria-sort')).toBe('descending')
    expect(host.textContent).toContain('sorted by the server')
    await click(q('vz-sort-top-1')!)
    expect(asked[3]!.order).toEqual({})
    expect(names()).toEqual(['b', 'a', 'c', 'd'])
  })

  it('a client that does not sort on the server (the fixtures) sorts the rows loaded and says so', async () => {
    const { asked, client } = serverClient(false)
    await act(async () => root.render(<DashboardView name="board" document={document_} client={client} theme="light" sortable />))
    await click(q('vz-sort-top-1')!)
    expect(asked).toHaveLength(1)
    expect(names()).toEqual(['a', 'd', 'c', 'b'])
    expect(host.textContent).toContain('Sorted over the 4 rows loaded, not all 9')
  })

  it('without `sortable` the headers are plain text and nothing is asked', async () => {
    const { asked, client } = serverClient(true)
    await act(async () => root.render(<DashboardView name="board" document={document_} client={client} theme="light" />))
    expect(q('vz-sort-top-1')).toBeNull()
    expect(asked).toHaveLength(1)
  })

  it('a refusal of the server is said and the rows stay', async () => {
    const { client } = serverClient(true)
    const refusing: ViewsClient = { ...client, async resolve(ref, layout, parameters, options) { if (options?.order) throw new Error('The table does not show `x`.'); return client.resolve(ref, layout, parameters, options) } }
    await act(async () => root.render(<DashboardView name="board" document={document_} client={refusing} theme="light" sortable />))
    await click(q('vz-sort-top-1')!)
    expect(q('vz-interaction-notice')!.textContent).toContain('could not be sorted by the server: The table does not show `x`.')
    expect(names()).toEqual(['b', 'a', 'c', 'd'])
  })
})
