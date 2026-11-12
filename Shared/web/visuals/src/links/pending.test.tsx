// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureClient, type FixtureBundle } from '../client/fixtureClient'
import { validateText } from '../client/text'
import { DashboardView, type DashboardViewProps } from '../dashboard/DashboardView'
import { describeLink, type LinkDoc } from '../views/links'
import { resolveAction } from '../views/actions'
import type { Box, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import type { ActionHost, ActionRequest, BatchOutcome, BatchRequest } from './actions'
import { createPendingStore, groupLines, stageChange, stagedFrom, type StagedChange } from './pending'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const text = `
schemaVersion: 1
kind: dashboard
title: Board
links:
  bump:
    change: {model: sales, entity: orders, edit: {key: $orders.id, version: $orders.version}, set: {units: $ask.units}}
    ask: {units: {type: integer}}
    confirm: {title: "Set units of {{orders.number}}"}
    stage: true
  close:
    change: {model: sales, entity: orders, edit: {key: $orders.id, version: $orders.version}, set: {status: Closed}}
    confirm: {title: "Close order {{orders.number}}"}
    stage: true
  drop:
    change: {model: sales, entity: orders, delete: {key: $orders.id, version: $orders.version}}
    confirm: {title: "Delete order {{orders.number}}"}
    stage: true
  other:
    change: {model: crm, entity: accounts, edit: {key: $orders.id}, set: {tier: Gold}}
    stage: true
  quick:
    change: {model: sales, entity: orders, edit: {key: $orders.id, version: $orders.version}, set: {flag: true}}
    confirm: skip
widgets:
  orders:
    type: table
    title: Orders
    encodings: {columns: [{field: orders.id, label: Id}, {field: orders.version, label: Version}, {field: orders.number, label: Number}]}
    query: {model: sales}
    on_click: close
  grid: {type: custom, title: Grid, component: sales-portal/Grid, links: [bump, other, drop, quick]}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 8}
    tiles:
      - {widget: orders, x: 0, y: 0, w: 12, h: 3}
      - {widget: grid, x: 0, y: 3, w: 12, h: 4}
`
const document_ = validateText(text).document as ViewsDoc
const cell = (v: string | number) => ({ v, text: String(v) })
const table: RenderedWidget = {
  type: 'table',
  columns: [{ field: 'orders.id', label: 'Id' }, { field: 'orders.version', label: 'Version' }, { field: 'orders.number', label: 'Number' }],
  rows: [{ cells: [cell(1), cell(1), cell('SO-1')] }, { cells: [cell(2), cell(5), cell('SO-2')] }],
}
const results: Record<string, RenderedWidget> = { orders: table, grid: { type: 'custom', component: 'sales-portal/Grid', data: {} } }
const bundle: FixtureBundle = { documents: { 'dashboard/board': text }, results: { 'dashboard/board': results } }

class FakeObserver { observe() {} unobserve() {} disconnect() {} }

describe('the pending set: what may join it', () => {
  const docs = document_.links as Record<string, LinkDoc>
  const line = (link: string, id: number, answers: Record<string, string> = {}): StagedChange => {
    const info = describeLink(link, docs[link]!)
    const row = { 'orders.id': id, 'orders.version': 1, 'orders.number': `SO-${id}` }
    const resolved = resolveAction(link, { action: info.action!, label: info.label }, row, {}, answers)
    return stagedFrom(resolved, { link, widget: 'grid', row, parameters: {}, answers }) as StagedChange
  }

  it('a change of one model becomes a line with the popup\'s title, the key, the version and the values', () => {
    const l = line('bump', 7, { units: '12' })
    expect(l).toMatchObject({ link: 'bump', model: 'sales', entity: 'orders', operation: 'edit', label: 'Set units of SO-7', version: '1', key: [{ name: 'key', value: '7' }], writes: [{ name: 'units', value: '12' }] })
  })

  it('the same link for the same row replaces its line; another row adds one', () => {
    let set = stageChange([], line('bump', 1, { units: '1' }))
    expect(set).toMatchObject({ ok: true, replaced: false })
    set = stageChange((set as { lines: StagedChange[] }).lines, line('bump', 1, { units: '2' }))
    expect(set).toMatchObject({ ok: true, replaced: true })
    expect((set as { lines: StagedChange[] }).lines).toHaveLength(1)
    expect((set as { lines: StagedChange[] }).lines[0]!.writes).toEqual([{ name: 'units', value: '2' }])
    set = stageChange((set as { lines: StagedChange[] }).lines, line('bump', 2, { units: '3' }))
    expect((set as { lines: StagedChange[] }).lines).toHaveLength(2)
  })

  it('a set over two models is refused naming both, and a set over the limit is refused', () => {
    const one = (stageChange([], line('close', 1)) as { lines: StagedChange[] }).lines
    expect(stageChange(one, line('other', 1))).toMatchObject({ ok: false, code: 'two_models', message: expect.stringContaining('`sales`') })
    expect((stageChange(one, line('other', 1)) as { message: string }).message).toContain('`crm`')
    expect(stageChange(one, line('close', 2), 1)).toMatchObject({ ok: false, code: 'limit' })
    expect(stageChange(one, line('close', 2), 2)).toMatchObject({ ok: true })
  })

  it('a delete drops the edits staged for the row, and an edit of a row staged for delete is refused', () => {
    let set = (stageChange([], line('close', 1)) as { lines: StagedChange[] }).lines
    set = (stageChange(set, line('bump', 1, { units: '4' })) as { lines: StagedChange[] }).lines
    expect(set).toHaveLength(2)
    set = (stageChange(set, line('drop', 1)) as { lines: StagedChange[] }).lines
    expect(set.map((l) => l.link)).toEqual(['drop'])
    expect(stageChange(set, line('close', 1))).toMatchObject({ ok: false, code: 'staged_for_delete' })
  })

  it('a task, or a row that lacks its key, is not staged', () => {
    const task = { link: 't', kind: 'task' as const, operation: 'run' as const, title: '', button: '', destructive: false, target: 'a.b', shows: [], writes: [], key: [], version: null, missing: [], asks: [] }
    expect(stagedFrom(task, { link: 't', widget: 'w', row: {}, parameters: {}, answers: {} })).toMatchObject({ ok: false, code: 'not_a_change' })
    const info = describeLink('close', docs.close!)
    expect(stagedFrom(resolveAction('close', { action: info.action! }, {}, {}, {}), { link: 'close', widget: 'w', row: {}, parameters: {}, answers: {} })).toMatchObject({ ok: false, code: 'missing_values' })
  })

  it('the review groups the lines by entity and row, and the store tells a listener', () => {
    const store = createPendingStore()
    let heard = 0
    store.subscribe(() => heard++)
    store.stage(line('close', 1))
    store.stage(line('bump', 1, { units: '2' }))
    store.stage(line('close', 2))
    expect(groupLines(store.get()).map((g) => [g.entity, g.rows.map((r) => r.lines.length)])).toEqual([['orders', [2, 1]]])
    store.remove(store.get()[0]!.id)
    store.clear()
    expect([store.get().length, heard]).toEqual([0, 5])
  })
})

describe('the shell stages changes, reviews them once and submits them as one batch (DBR53b)', () => {
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
  const batches: BatchRequest[] = []
  const singles: ActionRequest[] = []
  const calls: string[][] = []
  let outcome: BatchOutcome = { status: 'applied', count: 0 }
  let hold: (() => void) | null = null
  let willHold = false
  const actionHost: ActionHost = {
    run: async (r) => { singles.push(r); return { status: 'applied' } },
    submit: async (r) => { batches.push(r); if (willHold) await new Promise<void>((ok) => { hold = ok }); return outcome.status === 'applied' ? { status: 'applied', count: r.lines.length } : outcome },
    forecastBatch: async (r) => ({ will: 'wait', note: `This will wait for approval as one request of ${r.lines.length} changes.` }),
  }
  let current = fixtureClient(bundle)
  beforeEach(() => { batches.length = 0; singles.length = 0; calls.length = 0; outcome = { status: 'applied', count: 0 }; hold = null; willHold = false; current = fixtureClient(bundle, { onResolve: (c) => calls.push([...c.widgets]) }) })
  const show = (props: Partial<DashboardViewProps> = {}) => act(async () => root.render(<DashboardView name="board" document={document_} theme="light" box={box} client={current} actionHost={actionHost} customViews={{ frameSrc: () => '/apps/sales/_views/frame.html' }} {...props} />))
  const q = (id: string) => host.ownerDocument.querySelector(`[data-testid="${id}"]`) as HTMLElement | null
  const click = (el: Element) => act(async () => { el.dispatchEvent(new window.MouseEvent('click', { bubbles: true })) })
  const row = (name: string) => [...host.querySelectorAll('[data-testid="vz-table-orders"] tbody tr')].find((e) => e.textContent!.includes(name))!
  const frame = () => {
    const f = q('vz-custom-frame-grid') as HTMLIFrameElement
    const fake = { postMessage: () => {} }
    Object.defineProperty(f, 'contentWindow', { configurable: true, get: () => fake })
    return { say: (data: unknown) => act(async () => { window.dispatchEvent(new window.MessageEvent('message', { data, source: fake as unknown as Window })) }) }
  }
  const stage = async (say: (d: unknown) => Promise<void>, n: number, link = 'bump') => {
    for (let i = 1; i <= n; i++) await say({ type: 'views:stage', link, row: { 'orders.id': i, 'orders.version': 1, 'orders.number': `SO-${i}` }, answers: { units: String(i * 10) } })
  }

  it('a custom widget stages 30 edits, the bar says 30 changes, one review lists them and one confirmation sends all 30 in one request', async () => {
    await show()
    const { say } = frame()
    await say({ type: 'views:hello' })
    await stage(say, 30)
    expect(q('vz-pending-bar')!.getAttribute('data-count')).toBe('30')
    expect(q('vz-pending-count')!.textContent).toBe('30 changes')
    expect(batches).toHaveLength(0)                                     // nothing is sent while staging
    expect(q('vz-action')).toBeNull()                                   // and no popup of its own per edit
    await click(q('vz-pending-review')!)
    expect(q('vz-pending-title')!.textContent).toBe('Review 30 changes')
    expect(q('vz-pending-entity-orders')!.querySelectorAll('tr')).toHaveLength(30)
    expect(q('vz-pending-line-0')!.textContent).toContain('units: 10')
    expect(q('vz-pending-forecast')!.textContent).toBe('This will wait for approval as one request of 30 changes.')
    expect(q('vz-dashboard-host')!.hasAttribute('inert')).toBe(true)
    await click(q('vz-pending-submit')!)
    expect(batches).toHaveLength(1)
    expect(batches[0]!.lines).toHaveLength(30)
    expect(batches[0]!.lines[2]).toMatchObject({ link: 'bump', widget: 'grid', row: { 'orders.id': 3 }, answers: { units: '30' } })
    expect(batches[0]!.idempotencyKey.length).toBeGreaterThan(8)
    expect(q('vz-pending-done')!.textContent).toContain('30 changes were made together')
    expect(q('vz-pending-bar')).toBeNull()                              // an accepted submit clears the set
    expect(calls.at(-1)).toEqual(['grid'])                              // and the widget that staged resolves again
  })

  it('a double click on submit is one call', async () => {
    willHold = true
    await show()
    const { say } = frame()
    await say({ type: 'views:hello' })
    await stage(say, 3)
    await click(q('vz-pending-review')!)
    const submit = q('vz-pending-submit')!
    await click(submit); await click(submit)
    expect(batches).toHaveLength(1)
    await act(async () => hold?.())
  })

  it('a set with one invalid value applies none: the Store\'s reason is shown against that line and the set is kept', async () => {
    outcome = { status: 'refused', message: 'The changes break the model\'s rules, so none was made.', problems: [{ line: 1, rule: 'max', attribute: 'units', message: 'units is above the maximum of 100.' }] }
    await show()
    const { say } = frame()
    await say({ type: 'views:hello' })
    await stage(say, 3)
    await click(q('vz-pending-review')!)
    await click(q('vz-pending-submit')!)
    expect(q('vz-pending-failed')!.getAttribute('data-status')).toBe('refused')
    expect(q('vz-pending-problem-1')!.textContent).toBe('units: units is above the maximum of 100.')
    expect(q('vz-pending-problem-0')).toBeNull()
    expect(q('vz-pending-line-1')!.hasAttribute('data-problem')).toBe(true)
    expect(q('vz-pending-count')?.textContent ?? '3 changes').toBe('3 changes')
    // remove the broken line and submit the rest with the same key: the retry is the same call
    await click(q('vz-pending-remove-1')!)
    expect(q('vz-pending-title')!.textContent).toBe('Review 2 changes')
    outcome = { status: 'applied', count: 0 }
    await click(q('vz-pending-submit')!)
    expect(batches).toHaveLength(2)
    expect(batches[1]!.lines).toHaveLength(2)
    expect(q('vz-pending-done')).not.toBeNull()
  })

  it('a model that asks for approval keeps one request of N changes', async () => {
    outcome = { status: 'waiting', count: 3, requestId: 'cr-1' }
    await show()
    const { say } = frame()
    await say({ type: 'views:hello' })
    await stage(say, 3)
    await click(q('vz-pending-review')!)
    await click(q('vz-pending-submit')!)
    expect(q('vz-pending-done')!.getAttribute('data-status')).toBe('waiting')
    expect(q('vz-pending-done')!.textContent).toContain('one request of 3 changes')
  })

  it('clicking a row of a widget whose link stages adds a line; a set over two models is refused naming both', async () => {
    await show()
    await click(row('SO-1'))
    await click(row('SO-2'))
    expect(q('vz-pending-count')!.textContent).toBe('2 changes')
    expect(q('vz-action')).toBeNull()
    expect(row('SO-1').hasAttribute('data-pending')).toBe(true)         // the rows with a staged change are marked
    const { say } = frame()
    await say({ type: 'views:hello' })
    await say({ type: 'views:stage', link: 'other', row: { 'orders.id': 1 }, answers: {} })
    expect(q('vz-interaction-notice')!.textContent).toContain('`sales`')
    expect(q('vz-interaction-notice')!.textContent).toContain('`crm`')
    expect(q('vz-pending-count')!.textContent).toBe('2 changes')
  })

  it('the widget can ask for the review but cannot confirm, and can discard; it reads what is staged', async () => {
    await show()
    const { say } = frame()
    await say({ type: 'views:hello' })
    await stage(say, 2)
    await say({ type: 'views:submit' })
    expect(q('vz-pending')).not.toBeNull()
    expect(batches).toHaveLength(0)                                     // submit only opened the review
    await click(q('vz-pending-close')!)
    await say({ type: 'views:discard' })
    expect(q('vz-pending-bar')).toBeNull()
  })

  it('a discard empties the set; leaving the page with changes pending asks, with none it does not', async () => {
    await show()
    const { say } = frame()
    await say({ type: 'views:hello' })
    const asked = () => { const e = new window.Event('beforeunload', { cancelable: true }); window.dispatchEvent(e); return e.defaultPrevented }
    expect(asked()).toBe(false)
    await stage(say, 2)
    expect(asked()).toBe(true)
    await click(q('vz-pending-discard')!)
    expect(q('vz-pending-bar')).toBeNull()
    expect(asked()).toBe(false)
  })

  it('confirm: skip applies a field edit at once, without a popup or the set', async () => {
    await show()
    const { say } = frame()
    await say({ type: 'views:hello' })
    await say({ type: 'views:open', link: 'quick', row: { 'orders.id': 1, 'orders.version': 1, 'orders.number': 'SO-1' } })
    expect(singles).toHaveLength(1)
    expect(singles[0]).toMatchObject({ link: 'quick', row: { 'orders.id': 1 } })
    expect(q('vz-action')).toBeNull()
    expect(q('vz-pending-bar')).toBeNull()
  })
})
