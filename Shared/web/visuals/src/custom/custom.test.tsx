// @vitest-environment happy-dom
import { act, createElement } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureClient, type FixtureBundle } from '../client/fixtureClient'
import { validateText } from '../client/text'
import { DashboardView, type LinkEvent } from '../dashboard/DashboardView'
import type { Box, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { startViewFrame, useOpen, useSetParam, useView, type HostPort, type ToHost as SdkToHost, type FromHost as SdkFromHost } from '../../../views-sdk/src/index'
import { createFrameHost } from './controller'
import type { CustomViewsHost } from './CustomWidgetFrame'
import { checkSetParam, hostLinkInfo, readToHost, themeTokens, type FromHost, type ToHost, type ViewProps } from './protocol'
import { describeLink } from '../views/links'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const definitions = [{ name: 'region', type: 'string' as const, many: true }, { name: 'year', type: 'integer' as const }]

describe('readToHost: what a frame may say', () => {
  it('reads the six shapes and nothing else', () => {
    expect(readToHost({ type: 'views:hello' })).toEqual({ type: 'views:hello' })
    expect(readToHost({ type: 'views:ready', extra: 1 })).toEqual({ type: 'views:ready' })
    expect(readToHost({ type: 'views:error', message: 'x'.repeat(5000) })).toEqual({ type: 'views:error', message: 'x'.repeat(2000) })
    expect(readToHost({ type: 'views:size', height: 400.4 })).toEqual({ type: 'views:size', height: 400 })
    expect(readToHost({ type: 'views:setParam', name: 'region', value: ['East'] })).toEqual({ type: 'views:setParam', name: 'region', value: ['East'] })
    expect(readToHost({ type: 'views:open', link: 'detail', row: { id: 1, name: 'a', x: { deep: 1 } } })).toEqual({ type: 'views:open', link: 'detail', row: { id: 1, name: 'a' } })
  })
  it('drops what is not of those shapes', () => {
    for (const bad of [null, 'views:ready', 3, [], {}, { type: 'views:other' }, { type: 'views:error', message: 4 }, { type: 'views:size', height: -1 }, { type: 'views:size', height: Number.NaN },
      { type: 'views:setParam', name: 'r', value: { a: 1 } }, { type: 'views:setParam', name: 'r', value: [{}] }, { type: 'views:setParam', value: 'x' }, { type: 'views:open', link: 3 }, { type: 'views:open', link: 'a', row: [] }])
      expect(readToHost(bad)).toBeNull()
  })
})

describe('checkSetParam: a view sets only what the document declares, as the document types it', () => {
  it('accepts a declared parameter of its type, a list for a many one, and null as the default', () => {
    expect(checkSetParam(definitions, 'region', ['East'])).toEqual({ ok: true, value: ['East'] })
    expect(checkSetParam(definitions, 'region', 'East')).toEqual({ ok: true, value: ['East'] })
    expect(checkSetParam(definitions, 'region', null)).toEqual({ ok: true, value: [] })
    expect(checkSetParam(definitions, 'year', 2026)).toEqual({ ok: true, value: 2026 })
  })
  it('refuses an undeclared name, a wrong type and a list for a single value', () => {
    expect(checkSetParam(definitions, 'secret', 'x')).toMatchObject({ ok: false, message: expect.stringContaining('does not declare') })
    expect(checkSetParam(definitions, 'year', 'abc')).toMatchObject({ ok: false })
    expect(checkSetParam(definitions, 'year', 2026.5)).toMatchObject({ ok: false })
    expect(checkSetParam(definitions, 'year', [2026])).toMatchObject({ ok: false, message: expect.stringContaining('one value') })
    expect(checkSetParam(undefined, 'region', 'x')).toMatchObject({ ok: false })
  })
})

const props = (): ViewProps => ({ data: { orders: { columns: ['a'], rows: [{ a: 1 }] } }, params: {}, theme: { scheme: 'light', tokens: themeTokens('light') }, size: { width: 300, height: 200 }, mode: 'screen', links: {} })

describe('createFrameHost: the host side of one frame', () => {
  function make(extra: { openable?: string[] } = {}) {
    const posted: FromHost[] = []
    const events: string[] = []
    const host = createFrameHost({
      name: 'OrdersGrid', post: (m) => posted.push(m), props, definitions, openable: new Set(extra.openable ?? ['detail']),
      onSetParam: (n, v) => events.push(`param ${n}=${JSON.stringify(v)}`), onOpen: (l, r) => events.push(`open ${l} ${JSON.stringify(r)}`), onState: (s) => events.push(`state ${s.kind}`),
      onSize: (h) => events.push(`size ${h}`), onNotice: (m) => events.push(`notice ${m}`),
    })
    return { host, posted, events }
  }
  it('answers hello with init, then ready makes it ready; ready before hello does nothing', () => {
    const { host, posted, events } = make()
    host.receive({ type: 'views:ready' })
    expect(events).toEqual([])
    host.receive({ type: 'views:hello' })
    expect(posted).toEqual([{ type: 'views:init', name: 'OrdersGrid', props: props() }])
    host.receive({ type: 'views:ready' })
    expect(events).toEqual(['state ready'])
    host.sendProps()
    expect(posted[1]).toMatchObject({ type: 'views:props' })
  })
  it('an error is final', () => {
    const { host, events } = make()
    host.receive({ type: 'views:hello' })
    host.receive({ type: 'views:error', message: 'boom' })
    host.receive({ type: 'views:ready' })
    expect(events).toEqual(['state error'])
    expect(host.state).toEqual({ kind: 'error', message: 'boom' })
  })
  it('a parameter is set only when declared; a link is opened only when the widget lists it', () => {
    const { host, events } = make()
    host.receive({ type: 'views:setParam', name: 'region', value: ['East'] })
    host.receive({ type: 'views:setParam', name: 'hidden', value: 'x' })
    host.receive({ type: 'views:open', link: 'detail', row: { id: 7 } })
    host.receive({ type: 'views:open', link: 'other', row: {} })
    expect(events[0]).toBe('param region=["East"]')
    expect(events[1]).toContain('does not declare')
    expect(events[2]).toBe('open detail {"id":7}')
    expect(events[3]).toContain('does not list')
  })
})

// ---- the code's side (the SDK) against the host's side, over an in-memory port ----

function pair(host: ReturnType<typeof createFrameHost> | null, toHost: ToHost[]) {
  const listeners = new Set<(m: SdkFromHost) => void>()
  const frameSide: HostPort = {
    send: (m: SdkToHost) => { toHost.push(m as ToHost); const read = readToHost(m); if (read && host) host.receive(read) },
    subscribe: (l) => { listeners.add(l); return () => listeners.delete(l) },
  }
  return { frameSide, deliver: (m: FromHost) => { for (const l of listeners) l(m as SdkFromHost) } }
}

describe('the SDK frame runtime against the host controller', () => {
  let el: HTMLElement
  beforeEach(() => { el = window.document.createElement('div'); window.document.body.appendChild(el) })
  afterEach(() => { el.remove() })

  it('a document that is not a view frame mounts nothing', () => {
    expect(startViewFrame({ root: el, port: pair(null, []).frameSide, request: null })).toBe(false)
  })

  it('hello, init, the registered component draws the rows, and its setParam and open reach the host', async () => {
    const { defineWidget } = await import('../../../views-sdk/src/index')
    const sent: ToHost[] = []
    const events: string[] = []
    let deliver: (m: FromHost) => void = () => {}
    const host = createFrameHost({
      name: 'LoopGrid', post: (m) => deliver(m), props, definitions, openable: new Set(['detail']),
      onSetParam: (n, v) => events.push(`param ${n}=${JSON.stringify(v)}`), onOpen: (l, r) => events.push(`open ${l} ${JSON.stringify(r)}`), onState: (s) => events.push(`state ${s.kind}`), onSize: (h) => events.push(`size ${typeof h}`),
    })
    const p = pair(host, sent)
    deliver = p.deliver
    defineWidget('LoopGrid', function LoopGrid() {
      const view = useView()
      const set = useSetParam()
      const open = useOpen()
      return createElement('button', { 'data-testid': 'cell', onClick: () => { set('region', ['East']); open('detail', { id: 3 }) } }, `${view.data.orders!.rows.length} rows, ${view.mode}`)
    })
    let started = false
    await act(async () => { started = startViewFrame({ root: el, port: p.frameSide, request: { view: 'LoopGrid', mode: 'screen' } }) })
    expect(started).toBe(true)
    expect(sent[0]).toEqual({ type: 'views:hello' })
    const button = el.querySelector('[data-testid="cell"]') as HTMLButtonElement
    expect(button.textContent).toBe('1 rows, screen')
    // the view's links are the host's: this props has none, so open() is a no-op until the host lists one
    await act(async () => { button.click() })
    expect(events).toContain('param region=["East"]')
    expect(events.some((e) => e.startsWith('open'))).toBe(false)
    // the host sends props with a link; the same click now opens it
    const link = hostLinkInfo(describeLink('detail', { form: 'orders', edit: { id: '$id' }, as: 'dialog' }), null)
    await act(async () => { p.deliver({ type: 'views:props', props: { ...props(), links: { detail: link } } }) })
    await act(async () => { button.click() })
    expect(events).toContain('open detail {"id":3}')
  })
})

// ---- the dashboard with a custom widget ----

const text = `
schemaVersion: 1
kind: dashboard
title: Board
parameters:
  - {name: region, type: string, for: customers.region, many: true, default: [], label: Region}
links:
  edit-order: {form: orders, edit: {id: $id}, as: dialog}
widgets:
  grid:
    type: custom
    title: Regions
    component: sales-portal/RegionGrid
    params: {region: $params.region}
    links: [edit-order]
  net:
    type: metric
    label: Net sales
    encodings: {value: sales.net_amount}
    query: {model: retail_sales, filters: [{attribute: customers.region, operator: equals, values: {param: region}}]}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 4}
    tiles:
      - {widget: grid, x: 0, y: 0, w: 6, h: 4}
      - {widget: net, x: 6, y: 0, w: 6, h: 4}
`
const document_ = validateText(text).document as ViewsDoc
const rows = { by_region: { columns: ['customers_region'], rows: [{ customers_region: 'West' }, { customers_region: 'East' }] } }
const results: Record<string, RenderedWidget> = {
  grid: { type: 'custom', component: 'sales-portal/RegionGrid', data: rows },
  net: { type: 'metric', value: { v: 1, text: '1' } },
}
const bundle: FixtureBundle = { documents: { 'dashboard/board': text }, results: { 'dashboard/board': results } }

describe('DashboardView with a custom widget', () => {
  let host: HTMLDivElement
  let root: Root
  beforeEach(() => { host = window.document.createElement('div'); window.document.body.appendChild(host); root = createRoot(host) })
  afterEach(async () => { await act(async () => root.unmount()); host.remove() })

  const box: Box = { width: 1200, height: 600 }
  const frames: CustomViewsHost = { frameSrc: (c) => (c.startsWith('sales-portal/') ? '/apps/sales/_views/frame.html' : null), route: () => '/apps/sales/forms/orders' }
  const calls: string[][] = []
  const counting = (over: Record<string, RenderedWidget> = {}) => {
    calls.length = 0
    return fixtureClient({ ...bundle, results: { 'dashboard/board': { ...results, ...over } } }, { onResolve: (c) => calls.push([...c.widgets]) })
  }
  const show = (props: Partial<Parameters<typeof DashboardView>[0]> = {}) => act(async () => root.render(<DashboardView name="board" document={document_} theme="light" box={box} client={counting()} customViews={frames} {...props} />))
  const frameWindow = () => {
    const iframe = host.querySelector('[data-testid="vz-custom-frame-grid"]') as HTMLIFrameElement
    const posted: FromHost[] = []
    const fake = { postMessage: (m: FromHost) => posted.push(m) }
    Object.defineProperty(iframe, 'contentWindow', { configurable: true, get: () => fake })
    const say = (data: unknown, source: unknown = fake) => act(async () => { window.dispatchEvent(new window.MessageEvent('message', { data, source: source as Window })) })
    return { iframe, posted, say }
  }

  it('draws a sandboxed frame from the app\'s own address with the view and the mode in the fragment', async () => {
    await show()
    const iframe = host.querySelector('[data-testid="vz-custom-frame-grid"]') as HTMLIFrameElement
    expect(iframe.getAttribute('sandbox')).toBe('allow-scripts')
    expect(iframe.getAttribute('src')).toBe('/apps/sales/_views/frame.html#view=RegionGrid&mode=screen')
    expect(iframe.getAttribute('data-state')).toBe('loading')
  })

  it('without a host that serves the app, or for a component it does not serve, the widget says who draws it', async () => {
    await show({ customViews: undefined })
    expect(host.querySelector('[data-testid="vz-empty"]')?.textContent).toContain('Drawn by the app that owns it (sales-portal/RegionGrid)')
    await show({ customViews: { frameSrc: () => null } })
    expect(host.querySelector('[data-testid="vz-empty"]')?.textContent).toContain('sales-portal/RegionGrid')
  })

  it('gives the frame its rows, the document\'s link and the theme on hello, and ignores a window that is not the frame', async () => {
    await show()
    const { posted, say } = frameWindow()
    await say({ type: 'views:hello' }, {})
    expect(posted).toEqual([])
    await say({ type: 'views:hello' })
    const init = posted[0] as Extract<FromHost, { type: 'views:init' }>
    expect(init).toMatchObject({ type: 'views:init', name: 'RegionGrid' })
    expect(init.props.data).toEqual(rows)
    expect(init.props.mode).toBe('screen')
    expect(init.props.theme.scheme).toBe('light')
    expect(init.props.links['edit-order']).toMatchObject({ as: 'dialog', route: null, values: { id: { kind: 'field', text: 'id' } } })
    expect(init.props.size.width).toBeGreaterThan(100)
    await say({ type: 'views:ready' })
    expect(host.querySelector('[data-testid="vz-custom-frame-grid"]')?.getAttribute('data-state')).toBe('ready')
  })

  it('a selection in the custom widget re-runs the standard widget that uses the parameter, and only it', async () => {
    const told: Array<[string, unknown]> = []
    await show({ onParameterChange: (p, v) => told.push([p, v]) })
    const { say } = frameWindow()
    await say({ type: 'views:hello' })
    expect(calls).toEqual([['grid', 'net']])
    await say({ type: 'views:setParam', name: 'region', value: ['East'] })
    expect(told).toEqual([['region', ['East']]])
    expect(calls.length).toBe(2)
    expect([...calls[1]!].sort()).toEqual(['grid', 'net'])       // both read `region`: the custom widget through its params, the metric through its filter
    await say({ type: 'views:setParam', name: 'nope', value: 'x' })
    expect(told.length).toBe(1)
    expect(host.querySelector('[data-testid="vz-interaction-notice"]')?.textContent).toContain('does not declare')
  })

  it('a link the widget lists reaches the host with the values read from the row; one it does not list does not', async () => {
    const events: LinkEvent[] = []
    await show({ onLink: (e) => events.push(e) })
    const { posted, say } = frameWindow()
    await say({ type: 'views:hello' })
    await say({ type: 'views:open', link: 'edit-order', row: { id: 42, customers_region: 'East' } })
    expect(events.length).toBe(1)
    expect(events[0]).toMatchObject({ widget: 'grid', values: { id: 42 }, route: null })
    expect(events[0]!.link).toMatchObject({ name: 'edit-order', as: 'dialog', targetKind: 'form', target: 'orders' })
    await say({ type: 'views:open', link: 'secret', row: {} })
    expect(events.length).toBe(1)
    expect(posted.length).toBe(1)
  })

  it('a closed dialog is told to the widget that opened it', async () => {
    await show()
    const { posted, say } = frameWindow()
    await say({ type: 'views:hello' })
    await show({ closedLink: { widget: 'grid', link: 'edit-order', saved: true } })
    expect(posted.at(-1)).toEqual({ type: 'views:closed', link: 'edit-order', saved: true })
  })

  it('a view that reports an error shows the error tile and the metric beside it still draws', async () => {
    await show()
    const { say } = frameWindow()
    await say({ type: 'views:hello' })
    await say({ type: 'views:error', message: 'the grid could not be drawn' })
    expect(host.querySelector('[data-testid="vz-custom-frame-grid"]')).toBeNull()
    expect(host.querySelector('[data-testid="vz-widget-grid"] [data-testid="vz-widget-error"]')?.textContent).toContain('the grid could not be drawn')
    expect(host.querySelector('[data-testid="vz-widget-net"]')?.textContent).toContain('1')
  })

  it('a denied query is an error tile with the server\'s words, the frame is never made and the rest draws', async () => {
    await show({ client: counting({ grid: { type: 'error', code: 'denied', message: 'The query `by_region` of `sales-portal/RegionGrid`: The role may not read customers.' } }) })
    expect(host.querySelector('[data-testid="vz-custom-frame-grid"]')).toBeNull()
    expect(host.querySelector('[data-testid="vz-widget-grid"] [data-testid="vz-widget-error"]')?.textContent).toContain('may not read customers')
    expect(host.querySelector('[data-testid="vz-widget-net"]')?.textContent).toContain('1')
  })

  it('a frame that never starts fails its widget after the wait', async () => {
    const fake = await import('vitest')
    fake.vi.useFakeTimers()
    try {
      await show()
      await act(async () => { fake.vi.advanceTimersByTime(16000) })
      expect(host.querySelector('[data-testid="vz-widget-grid"] [data-testid="vz-widget-error"]')?.textContent).toContain('did not start')
    } finally { fake.vi.useRealTimers() }
  })

  it('a resize reaches the frame as new props', async () => {
    await show()
    const { posted, say } = frameWindow()
    await say({ type: 'views:hello' })
    await show({ box: { width: 800, height: 500 } })
    const last = posted.at(-1) as Extract<FromHost, { type: 'views:props' }>
    expect(last.type).toBe('views:props')
    expect(last.props.size.width).toBeLessThan((posted[0] as Extract<FromHost, { type: 'views:init' }>).props.size.width)
  })
})
