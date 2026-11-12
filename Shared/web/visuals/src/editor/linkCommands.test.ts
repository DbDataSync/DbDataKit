import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { parse } from 'yaml'
import { validateViewsDocument } from '../views/document'
import type { ViewsDoc } from '../views/model'
import { canonicalText } from './canonical'
import { EditError } from './commands'
import type { EditCommand } from './commands'
import { edit, editAll, newDraft } from './draft'
import type { Draft } from './draft'

const root = join(import.meta.dirname, '../../../../conformance/views')
const good = readFileSync(join(root, 'good/dashboard-links.json'), 'utf8')
const make = (value: unknown): Draft => newDraft('dashboard', 'links', canonicalText(value))
const start = (): Draft => make(JSON.parse(good))
const ok = (draft: Draft, command: EditCommand) => {
  const r = edit(draft, command)
  expect(r.error?.message).toBeUndefined()
  return r.draft
}
const value = (draft: Draft) => parse(draft.text) as ViewsDoc & { links: Record<string, Record<string, unknown>> }
const refused = (draft: Draft, command: EditCommand) => { const r = edit(draft, command); expect(r.error).toBeInstanceOf(EditError); return r.error as EditError }

describe('the canonical text of links', () => {
  it('writes the links after the cover and before the widgets, a short link on one line, its keys in the vocabulary order', () => {
    const text = canonicalText({ widgets: { w: { type: 'text', text: 'x' } }, links: { go: { label: 'Go', as: 'dialog', page: 'p', with: { a: '$x' } } }, title: 'T', kind: 'dashboard', schemaVersion: 1 })
    expect(text.indexOf('links:')).toBeLessThan(text.indexOf('widgets:'))
    expect(text).toContain('  go: {page: p, with: {a: $x}, as: dialog, label: Go}')
  })
  it('round-trips the good document', () => {
    const once = canonicalText(JSON.parse(good))
    expect(parse(once)).toEqual(JSON.parse(good))
    expect(canonicalText(parse(once))).toBe(once)
  })
})

describe('the link commands', () => {
  it('add a link, with a new document\'s first links map made as a block', () => {
    let d = make(({ schemaVersion: 1, kind: 'dashboard', title: 'T', widgets: { w: { type: 'text', text: 'x' } }, layouts: [{ name: 'd', type: 'reflow', default: true, grid: { columns: 12 }, tiles: [{ widget: 'w', x: 0, y: 0, w: 12, h: 2 }] }] }))
    d = ok(d, { type: 'addLink', name: 'go', link: { page: 'detail', with: { id: '$orders.id' } } })
    expect(value(d).links).toEqual({ go: { page: 'detail', with: { id: '$orders.id' } } })
    expect(d.text).toMatch(/\nlinks:\n {2}go: \{page: detail, with: \{id: \$orders\.id\}\}\n/)
    expect(validateViewsDocument(value(d), { kind: 'dashboard' })).toEqual([])
  })
  it('refuse a name that is taken, a bad name and a link that is not there', () => {
    const d = start()
    expect(refused(d, { type: 'addLink', name: 'order', link: { page: 'p' } }).code).toBe('name_taken')
    expect(refused(d, { type: 'addLink', name: 'Bad Name', link: { page: 'p' } }).code).toBe('bad_name')
    expect(refused(d, { type: 'setLinkOption', name: 'nope', key: 'as', value: 'panel' }).code).toBe('unknown_link')
  })
  it('rename a link and the on_click of the widgets that follow it', () => {
    const d = ok(start(), { type: 'renameLink', name: 'region-page', to: 'regions' })
    expect(Object.keys(value(d).links)).toContain('regions')
    expect(Object.keys(value(d).links)).not.toContain('region-page')
    expect((value(d).widgets.by_region as { on_click?: string }).on_click).toBe('regions')
    expect(validateViewsDocument(value(d), { kind: 'dashboard' })).toEqual([])
  })
  it('remove a link and the on_click that named it, and the whole map with the last link', () => {
    let d = ok(start(), { type: 'removeLink', name: 'region-page' })
    expect((value(d).widgets.by_region as { on_click?: string }).on_click).toBeUndefined()
    expect(validateViewsDocument(value(d), { kind: 'dashboard' })).toEqual([])
    for (const name of ['order-form', 'order', 'other-dashboard', 'a-view']) d = ok(d, { type: 'removeLink', name })
    d = ok(d, { type: 'removeLink', name: 'year-report' })
    expect(value(d).links).toBeUndefined()
    expect(validateViewsDocument(value(d), { kind: 'dashboard' })).toEqual([])
  })
  it('change the target: values follow between with and new, lock goes when the form is left', () => {
    let d = ok(start(), { type: 'setLinkTarget', name: 'order-form', kind: 'page', target: 'orders-page' })
    const link = value(d).links['order-form']!
    expect(link.page).toBe('orders-page')
    expect(link.form).toBeUndefined()
    expect(link.with).toEqual({ customer_id: '$customers.id', region: '$customers.region', status: 'open' })
    expect(link.new).toBeUndefined()
    expect(link.lock).toBeUndefined()
    expect(link.as).toBe('dialog')
    d = ok(d, { type: 'setLinkTarget', name: 'order-form', kind: 'form', target: 'orders' })
    expect(value(d).links['order-form']!.new).toEqual({ customer_id: '$customers.id', region: '$customers.region', status: 'open' })
    expect(Object.keys(value(d).links['order-form']!)[0]).toBe('form')
  })
  it('set and remove a value; removing a locked field unlocks it; the last value takes the group with it', () => {
    let d = ok(start(), { type: 'setLinkValue', name: 'region-page', param: 'year', value: 2027 })
    expect((value(d).links['region-page']!.with as Record<string, unknown>).year).toBe(2027)
    d = ok(d, { type: 'setLinkValue', name: 'order-form', param: 'region', value: undefined })
    expect(value(d).links['order-form']!.lock).toBeUndefined()
    expect(Object.keys(value(d).links['order-form']!.new as object)).toEqual(['customer_id', 'status'])
    d = ok(d, { type: 'setLinkValue', name: 'other-dashboard', param: 'x', value: '$params.region' })
    expect(value(d).links['other-dashboard']!.with).toEqual({ x: '$params.region' })
    d = ok(d, { type: 'setLinkValue', name: 'other-dashboard', param: 'x', value: undefined })
    expect(value(d).links['other-dashboard']!.with).toBeUndefined()
  })
  it('switch a form between new and edit, lock and unlock a field, set and clear the options', () => {
    let d = ok(start(), { type: 'setFormMode', name: 'order-form', mode: 'edit' })
    expect(value(d).links['order-form']!.edit).toBeDefined()
    expect(value(d).links['order-form']!.new).toBeUndefined()
    d = ok(d, { type: 'setLinkLock', name: 'order-form', field: 'status', locked: true })
    expect(value(d).links['order-form']!.lock).toEqual(['region', 'status'])
    d = ok(d, { type: 'setLinkLock', name: 'order-form', field: 'region', locked: false })
    d = ok(d, { type: 'setLinkLock', name: 'order-form', field: 'status', locked: false })
    expect(value(d).links['order-form']!.lock).toBeUndefined()
    expect(refused(d, { type: 'setLinkLock', name: 'region-page', field: 'region', locked: true }).code).toBe('not_a_form')
    d = ok(d, { type: 'setLinkOption', name: 'region-page', key: 'as', value: 'panel' })
    d = ok(d, { type: 'setLinkOption', name: 'region-page', key: 'print', value: undefined })
    d = ok(d, { type: 'setLinkOption', name: 'region-page', key: 'then', value: ['orders_table'] })
    const link = value(d).links['region-page']!
    expect([link.as, link.print, link.then]).toEqual(['panel', undefined, ['orders_table']])
  })
  it('the validator, not the command, says print: link in a dialog is wrong', () => {
    const d = ok(start(), { type: 'setLinkOption', name: 'region-page', key: 'as', value: 'dialog' })
    expect(validateViewsDocument(value(d), { kind: 'dashboard' }).map((i) => i.code)).toEqual(['DBD-VIEW-046'])
  })
  it('set the link a chart follows on a click; a drill written in place gives way; a metric has no click', () => {
    let d = ok(start(), { type: 'setOnClick', widget: 'by_region', link: 'order' })
    expect((value(d).widgets.by_region as { on_click?: string }).on_click).toBe('order')
    d = ok(d, { type: 'setOnClick', widget: 'by_region', link: undefined })
    expect((value(d).widgets.by_region as { on_click?: string }).on_click).toBeUndefined()
    const drilled = make(({ ...JSON.parse(good), widgets: { ...JSON.parse(good).widgets, by_region: { ...JSON.parse(good).widgets.by_region, on_click: undefined, drill: { to: { view: 'customers' } } } } }))
    const after = ok(drilled, { type: 'setOnClick', widget: 'by_region', link: 'region-page' })
    expect((value(after).widgets.by_region as { drill?: unknown }).drill).toBeUndefined()
    expect(validateViewsDocument(value(after), { kind: 'dashboard' })).toEqual([])
    const withMetric = make(({ ...JSON.parse(good), widgets: { ...JSON.parse(good).widgets, revenue: { type: 'metric', encodings: { value: 'a.b' }, query: { model: 'm' } } } }))
    expect(refused(withMetric, { type: 'setOnClick', widget: 'revenue', link: 'order' }).code).toBe('not_clickable')
    expect(refused(d, { type: 'setOnClick', widget: 'by_region', link: 'ghost' }).code).toBe('unknown_link')
  })
  it('a group of commands is one undo step', () => {
    const r = editAll(start(), [{ type: 'renameLink', name: 'order', to: 'open-order' }, { type: 'setLinkOption', name: 'open-order', key: 'as', value: 'dialog' }])
    expect(r.error?.message).toBeUndefined()
  })
})

describe('the action link commands (DBR53)', () => {
  const actions = readFileSync(join(root, 'good/dashboard-actions.json'), 'utf8')
  const begin = (): Draft => make(JSON.parse(actions))
  const codes = (d: Draft) => validateViewsDocument(value(d), { kind: 'dashboard' }).map((i) => i.code)

  it('add a change that edits a row and writes a value, a task, an insert and a delete, and the validator accepts each', () => {
    let d = begin()
    d = ok(d, { type: 'addActionLink', name: 'reopen', action: { kind: 'change', model: 'sales', entity: 'orders', operation: 'edit', key: '$orders.id', version: '$orders.version', values: { status: 'Open' } } })
    expect(value(d).links.reopen).toEqual({ change: { model: 'sales', entity: 'orders', edit: { key: '$orders.id', version: '$orders.version' }, set: { status: 'Open' } } })
    d = ok(d, { type: 'addActionLink', name: 'run-it', action: { kind: 'task', package: 'nightly', task: 'load', inputs: { days: 3 } } })
    expect(value(d).links['run-it']).toEqual({ task: { package: 'nightly', task: 'load', inputs: { days: 3 } } })
    d = ok(d, { type: 'addActionLink', name: 'add-it', action: { kind: 'change', model: 'sales', entity: 'orders', operation: 'insert', values: { status: 'Open' } } })
    d = ok(d, { type: 'addActionLink', name: 'drop-it', action: { kind: 'change', model: 'sales', entity: 'orders', operation: 'delete', key: '$orders.id' } })
    expect(value(d).links['drop-it']).toEqual({ change: { model: 'sales', entity: 'orders', delete: { key: '$orders.id' } } })
    expect(codes(d)).toEqual([])
    expect(refused(d, { type: 'addActionLink', name: 'reopen', action: { kind: 'task', package: 'a', task: 'b' } }).code).toBe('name_taken')
    expect(refused(d, { type: 'addActionLink', name: 'no-key', action: { kind: 'change', model: 'sales', entity: 'orders', operation: 'edit' } }).code).toBe('bad_name')
    expect(refused(d, { type: 'addActionLink', name: 'Bad Name', action: { kind: 'task', package: 'a', task: 'b' } }).code).toBe('bad_name')
  })

  it('set the popup\'s title and button, skip, stage and for; each is removed with an empty value', () => {
    let d = begin()
    d = ok(d, { type: 'setActionConfirm', name: 'close-order', key: 'title', value: 'Close it {{orders.number}}' })
    expect((value(d).links['close-order']!.confirm as Record<string, unknown>).title).toBe('Close it {{orders.number}}')
    expect((value(d).links['close-order']!.confirm as Record<string, unknown>).show).toEqual(['orders.number', 'orders.customer'])   // what was there stays
    d = ok(d, { type: 'setActionConfirm', name: 'close-order', key: 'button', value: '' })
    expect((value(d).links['close-order']!.confirm as Record<string, unknown>).button).toBeUndefined()
    d = ok(d, { type: 'setLinkOption', name: 'close-order', key: 'stage', value: true })
    d = ok(d, { type: 'setLinkOption', name: 'close-order', key: 'for', value: 'selection' })
    expect(value(d).links['close-order']).toMatchObject({ stage: true, for: 'selection' })
    expect(codes(d)).toEqual([])
    // skip replaces the popup's words and is refused by the validator where it cannot be (a delete)
    d = ok(d, { type: 'setLinkOption', name: 'close-order', key: 'stage', value: undefined })
    d = ok(d, { type: 'setLinkOption', name: 'close-order', key: 'for', value: undefined })
    d = ok(d, { type: 'setActionConfirm', name: 'close-order', key: 'skip', value: true })
    expect(value(d).links['close-order']!.confirm).toBe('skip')
    expect(codes(d)).toEqual([])
    d = ok(d, { type: 'setActionConfirm', name: 'drop-order', key: 'skip', value: true })
    expect(codes(d)).toEqual(['DBD-VIEW-066'])
    d = ok(d, { type: 'setActionConfirm', name: 'drop-order', key: 'skip', value: false })
    expect(codes(d)).toEqual([])
    d = ok(d, { type: 'addLink', name: 'go', link: { page: 'p' } })
    expect(refused(d, { type: 'setActionConfirm', name: 'go', key: 'title', value: 'x' }).code).toBe('not_an_action')
  })

  it('edit the values an edit sets, a task\'s inputs and an insert\'s fields; a delete has none', () => {
    let d = begin()
    d = ok(d, { type: 'setActionValue', name: 'close-order', field: 'closed_by', value: '$ask.who' })
    expect(codes(d)).toEqual(['DBD-VIEW-064'])                                   // `$ask.who` is not asked for: the validator says so
    d = ok(d, { type: 'setActionValue', name: 'close-order', field: 'closed_by', value: undefined })
    d = ok(d, { type: 'setActionValue', name: 'rerun-load', field: 'days', value: 9 })
    expect(((value(d).links['rerun-load']!.task as Record<string, unknown>).inputs as Record<string, unknown>).days).toBe(9)
    d = ok(d, { type: 'setActionValue', name: 'new-order', field: 'units', value: 4 })
    expect(((value(d).links['new-order']!.change as Record<string, unknown>).insert as Record<string, unknown>).units).toBe(4)
    expect(refused(d, { type: 'setActionValue', name: 'drop-order', field: 'x', value: 1 }).code).toBe('nothing_to_set')
    expect(codes(d)).toEqual([])
  })
})
