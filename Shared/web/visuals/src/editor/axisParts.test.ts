import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { parse } from 'yaml'
import { checkFormat } from '../views/format'
import type { CatalogRoles } from '../views/derive'
import type { ViewsDoc, WidgetDoc } from '../views/model'
import { sampleBundle } from '../testing/sampleBundle'
import { draftFromOpened, edit } from './draft'
import { localFileStore } from './store'
import { draftOf, draftProblem, formatChannelCommand, formatChannelsOf, formatOf, formatSample, levelCommand, levelEntry, levelsOf, sortValue } from './axisParts'

const bundle = sampleBundle()
const open = async () => draftFromOpened(await localFileStore(bundle.documents).open('dashboard', 'retail-sales-today'))
const must = (r: ReturnType<typeof edit>) => { expect(r.error?.message).toBeUndefined(); return r.draft }
const goldens = join(import.meta.dirname, '../../../../conformance/views/derive')
const golden = JSON.parse(readFileSync(join(goldens, 'good/chart-nested-axis-levels-with-a-sort-and-a-format.json'), 'utf8')) as { widgets: { w: WidgetDoc } }
const catalogs = (JSON.parse(readFileSync(join(goldens, 'cases.json'), 'utf8')) as { catalogs: Record<string, CatalogRoles> }).catalogs

describe('a format as a form (DBR44)', () => {
  it('reads every kind of stored format and writes back what it read', () => {
    for (const format of [undefined, 'compact', { style: 'currency', currency: 'EUR', decimals: 0 }, { style: 'number', grouping: false }, { date: 'MMM yyyy' }]) {
      expect(formatOf(draftOf(format))).toEqual(format)
    }
  })

  it('writes only the keys that were given, uppercases a currency, and says what the loaders say about a bad one', () => {
    const d = { ...draftOf({ style: 'currency', currency: 'usd' }), decimals: '2' }
    expect(formatOf(d)).toEqual({ style: 'currency', decimals: 2, currency: 'USD' })
    expect(draftProblem({ ...draftOf(undefined), kind: 'number', style: 'currency' })).toContain('currency')
    expect(draftProblem({ ...draftOf(undefined), kind: 'date', date: 'yyy' })).toContain('yyy')
    expect(draftProblem({ ...draftOf(undefined), kind: 'number', decimals: '1.5' })).toBe('`decimals` is a whole number.')
    expect(draftProblem(draftOf('percent'))).toBeNull()
    // whatever the form writes without a problem is a format the loaders read
    expect(checkFormat(formatOf({ ...draftOf(undefined), kind: 'number', style: 'currency', currency: 'EUR', decimals: '0' }))).toBeNull()
  })

  it('shows a sample written by the shared formatter, and none for a format that is refused', () => {
    expect(formatSample({ style: 'currency', currency: 'USD', decimals: 0 })).toBe('$1,234,568')
    expect(formatSample({ date: 'MMM' })).toBe('Mar')
    expect(formatSample({ date: 'dd/MM/yyyy' })).toBe('07/03/2026')
    expect(formatSample('compact')).toMatch(/1\.2/)
    expect(formatSample({ style: 'currency' })).toBe('')
  })
})

describe('the levels of an axis (DBR44)', () => {
  it('reads a field, an object, a list, and a scalar x as one level', () => {
    const w = { type: 'chart', mark: 'bar', encodings: { x: ['a.b', { field: 'c.d', format: { date: 'MMM' }, sort: { by: 'm.n', dir: 'desc' }, title: 'D' }] } } as unknown as WidgetDoc
    expect(levelsOf(w, 'x')).toEqual([{ field: 'a.b' }, { field: 'c.d', format: { date: 'MMM' }, sort: { by: 'm.n', dir: 'desc' }, title: 'D' }])
    expect(levelsOf({ type: 'chart', encodings: { x: 'a.b' } } as unknown as WidgetDoc, 'x')).toEqual([{ field: 'a.b' }])
    expect(levelsOf({ type: 'chart', encodings: {} } as unknown as WidgetDoc, 'x')).toEqual([])
  })

  it('an entry grows keys and goes back to the bare field when they are gone; the field stays first', () => {
    const withFormat = levelEntry('a.b', 'format', 'compact')
    expect(withFormat).toEqual({ field: 'a.b', format: 'compact' })
    expect(Object.keys(levelEntry(withFormat, 'sort', { by: 'sort_key' }) as object)).toEqual(['field', 'format', 'sort'])
    expect(levelEntry(withFormat, 'format', undefined)).toBe('a.b')
  })

  it('a sort is by and a direction, dir only when it is not the default', () => {
    expect(sortValue('', 'asc')).toBeUndefined()
    expect(sortValue('label', 'asc')).toEqual({ by: 'label' })
    expect(sortValue('orders.net_revenue', 'desc')).toEqual({ by: 'orders.net_revenue', dir: 'desc' })
  })

  it('the year-then-month chart built through the wells and the level controls is the golden widget', async () => {
    const start = { type: 'chart', mark: 'bar', encodings: { x: 'orders.ordered_at_year', y: 'orders.net_revenue', color: 'orders.status' }, query: { model: 'sales_catalog', limit: 200 } } as unknown as WidgetDoc
    const roles = catalogs.sales_catalog
    let d = must(edit(await open(), { type: 'addWidget', id: 'w', widget: start }))
    d = must(edit(d, { type: 'setWell', widget: 'w', well: 'x', change: { op: 'add', field: 'orders.ordered_at_month' }, roles }))
    const run = (index: number, key: 'format' | 'sort', value: unknown) => {
      const doc = parse(d.text) as ViewsDoc
      const c = levelCommand('w', doc.widgets.w as WidgetDoc, 'x', index, key, value)
      d = must(edit(d, { type: 'setPath', path: c.path, value: c.value }))
    }
    run(0, 'format', { date: 'year' })
    run(0, 'sort', sortValue('sort_key', 'desc'))
    run(1, 'format', { date: 'MMM' })
    run(1, 'sort', sortValue('label', 'asc'))
    const built = (parse(d.text) as ViewsDoc).widgets.w
    expect(built).toEqual(golden.widgets.w)
    // and a scalar x takes a format in place
    const scalar = levelCommand('w', start, 'x', 0, 'format', { date: 'year' })
    expect(scalar).toEqual({ path: ['widgets', 'w', 'encodings', 'x'], value: { field: 'orders.ordered_at_year', format: { date: 'year' } } })
  })
})

describe('the format of a measure channel and of a matrix value (DBR49)', () => {
  const chart = { type: 'chart', mark: 'bar', encodings: { x: 'customers.region', y: 'orders.net_revenue', color: { field: 'products.category', type: 'nominal' }, column: 'orders.status' }, query: { model: 'sales', limit: 20 } } as unknown as WidgetDoc
  const matrix = { type: 'matrix', encodings: { rows: ['customers.region'], columns: ['date.year'], values: [{ field: 'orders.net_revenue', label: 'Revenue' }, { field: 'orders.orders', format: 'compact' }] }, query: { model: 'sales', limit: 100 } } as unknown as WidgetDoc

  it('lists the channels that take a format, in the order the form shows them', () => {
    expect(formatChannelsOf(chart).map((c) => [c.channel, c.field, c.format])).toEqual([['y', 'orders.net_revenue', undefined], ['color', 'products.category', undefined], ['column', 'orders.status', undefined]])
    expect(formatChannelsOf(matrix).map((c) => [c.channel, c.index, c.field, c.format])).toEqual([['values', 0, 'orders.net_revenue', undefined], ['values', 1, 'orders.orders', 'compact']])
    expect(formatChannelsOf({ type: 'metric', encodings: { value: 'a.b' }, query: {} } as unknown as WidgetDoc)).toEqual([])
  })

  it('a bare channel becomes {field, format} and goes back when the format is removed; the other keys stay', () => {
    expect(formatChannelCommand('w', chart, 'y', 0, { style: 'currency', currency: 'EUR' })).toEqual({ path: ['widgets', 'w', 'encodings', 'y'], value: { field: 'orders.net_revenue', format: { style: 'currency', currency: 'EUR' } } })
    expect(formatChannelCommand('w', chart, 'color', 0, 'compact').value).toEqual({ field: 'products.category', type: 'nominal', format: 'compact' })
    const formatted = { ...chart, encodings: { ...(chart as unknown as { encodings: object }).encodings, y: { field: 'orders.net_revenue', format: 'compact' } } } as unknown as WidgetDoc
    expect(formatChannelCommand('w', formatted, 'y', 0, undefined).value).toBe('orders.net_revenue')
  })

  it('a matrix value stays an object, and keeps its label', () => {
    expect(formatChannelCommand('w', matrix, 'values', 0, 'percent')).toEqual({ path: ['widgets', 'w', 'encodings', 'values', 0], value: { field: 'orders.net_revenue', label: 'Revenue', format: 'percent' } })
    expect(formatChannelCommand('w', matrix, 'values', 1, undefined).value).toEqual({ field: 'orders.orders' })
  })

  it('the commands write the format into the widget and the saved widget is the golden, valid under the validator', async () => {
    let d = must(edit(await open(), { type: 'addWidget', id: 'w', widget: structuredClone(chart) }))
    const apply = (channel: 'y' | 'color' | 'column' | 'row' | 'values', value: unknown) => {
      const doc = parse(d.text) as ViewsDoc
      const c = formatChannelCommand('w', doc.widgets.w as WidgetDoc, channel, 0, value)
      d = must(edit(d, { type: 'setPath', path: c.path, value: c.value }))
    }
    apply('y', { style: 'currency', currency: 'EUR', decimals: 0 })
    apply('color', 'compact')
    apply('column', undefined)
    expect((parse(d.text) as ViewsDoc).widgets.w).toEqual({
      type: 'chart', mark: 'bar',
      encodings: { x: 'customers.region', y: { field: 'orders.net_revenue', format: { style: 'currency', currency: 'EUR', decimals: 0 } }, color: { field: 'products.category', type: 'nominal', format: 'compact' }, column: 'orders.status' },
      query: { model: 'sales', limit: 20 },
    })
    // the widget is on no layout (a dashboard-level finding); everything about the widget itself is valid
    expect(d.validation.diagnostics.filter((x) => x.code !== 'DBD-VIEW-023' && x.path.startsWith('widgets.w'))).toEqual([])
  })
})
