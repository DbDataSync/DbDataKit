import { describe, expect, it } from 'vitest'
import { parse } from 'yaml'
import { retailCatalog } from '../views/catalog'
import type { Catalog } from '../views/catalog'
import { deriveRequest } from '../views/derive'
import { catalogRoles } from '../views/catalog'
import type { ViewsDoc } from '../views/model'
import { sampleBundle } from '../testing/sampleBundle'
import { draftFromOpened, edit, undo } from './draft'
import { localFileStore } from './store'
import { filterEntry, filterProblems, operatorsFor, parameterValue, parseValues, sortAt } from './queryParts'

const bundle = sampleBundle()
const open = async () => draftFromOpened(await localFileStore(bundle.documents).open('dashboard', 'retail-sales-today'))
const must = (r: ReturnType<typeof edit>) => { expect(r.error?.message).toBeUndefined(); return r.draft }
const docOf = (d: { text: string }) => parse(d.text) as ViewsDoc
const catalog: Catalog = retailCatalog
const widget = (d: { text: string }, id: string) => docOf(d).widgets[id] as any

describe('operators by the type of the field', () => {
  const member = (name: string) => [...catalog.attributes, ...catalog.measures].find((m) => m.name === name)
  it('offers a string the text operators, a date the comparisons and the relative ones, a number the comparisons', () => {
    expect(operatorsFor(member('customers.region'))).toEqual(['equals', 'not_equals', 'contains', 'not_contains', 'starts_with', 'set', 'not_set'])
    expect(operatorsFor(member('date.calendar_date'))).toEqual(['equals', 'not_equals', 'gt', 'gte', 'lt', 'lte', 'between', 'set', 'not_set', 'last_complete', 'current', 'period_to_date'])
    expect(operatorsFor(member('date.year'))).toEqual(['equals', 'not_equals', 'gt', 'gte', 'lt', 'lte', 'between', 'set', 'not_set'])
    expect(operatorsFor({ name: 'p', time: true })).toContain('gte')
  })
})

describe('what is wrong with a filter, in the engine\'s words', () => {
  it('refuses an operator the type does not take', () => {
    expect(filterProblems({ attribute: 'customers.region', operator: 'gt', values: ['A'] }, { ...catalog, attributes: catalog.attributes.map((m) => (m.name === 'customers.region' ? { ...m, type: 'string' } : m)) }))
      .toEqual([{ code: 'invalid_operator', key: 'operator', message: "gt can't be used on string member customers.region." }])
  })
  it('flags a missing member, a measure as the attribute, a missing value and a bad count', () => {
    expect(filterProblems({ attribute: 'customers.nope', operator: 'equals', values: ['x'] }, catalog)[0]).toMatchObject({ code: 'unknown_member', message: 'customers.nope is not a catalog member.' })
    expect(filterProblems({ attribute: 'sales.net_amount', operator: 'equals', values: [1] }, catalog)[0]).toMatchObject({ code: 'wrong_member_kind', message: 'sales.net_amount is a measure, not a attribute.' })
    expect(filterProblems({ attribute: 'customers.region', operator: 'equals' }, catalog)).toEqual([{ code: 'missing_values', key: 'values', message: 'equals needs at least one value.' }])
    expect(filterProblems({ attribute: 'date.calendar_date', operator: 'last_complete' }, catalog)[0]).toMatchObject({ code: 'period_count_invalid' })
    expect(filterProblems({ attribute: 'date.calendar_date', operator: 'current', values: ['x'] }, catalog)[0]).toMatchObject({ code: 'period_operator_takes_values' })
    expect(filterProblems({ attribute: 'customers.region', operator: 'set' }, catalog)).toEqual([])
  })
  it('builds an entry without a value for an operator that takes none, and a parameter as a value', () => {
    expect(filterEntry('customers.region', 'set', ['x'])).toEqual({ attribute: 'customers.region', operator: 'set' })
    expect(parameterValue('region', [{ name: 'region', type: 'string', many: true }])).toEqual({ param: 'region' })
    expect(parameterValue('quarter', [{ name: 'quarter', type: 'string' }])).toEqual([{ param: 'quarter' }])
    expect(parseValues('a, b ,,3', undefined)).toEqual(['a', 'b', '3'])
    expect(parseValues('3, 4.5', { name: 'x', type: 'number' })).toEqual([3, 4.5])
  })
})

describe('the filter commands', () => {
  it('add a filter to a chart (the key is created), derive it in the request, and undo in one step', async () => {
    const draft = await open()
    const entry = filterEntry('customers.region', 'equals', ['West'])
    const next = must(edit(draft, { type: 'setFilter', widget: 'by_region', change: { op: 'add', filter: entry }, catalog }))
    expect(widget(next, 'by_region').query.filters).toEqual([entry])
    const request = deriveRequest({ id: 'by_region', raw: widget(next, 'by_region') }, catalogRoles(catalog)).request as any
    expect(request.query.filters).toEqual([entry])
    expect(undo(next).text).toBe(draft.text)
  })
  it('add a parameter as the value, replace, move and remove (the key goes with the last)', async () => {
    let d = await open()
    d = must(edit(d, { type: 'setFilter', widget: 'top_products', change: { op: 'add', filter: filterEntry('products.category', 'equals', parameterValue('region')) }, catalog }))
    expect(widget(d, 'top_products').query.filters).toHaveLength(2)
    d = must(edit(d, { type: 'setFilter', widget: 'top_products', change: { op: 'move', from: 1, to: 0 }, catalog }))
    expect(widget(d, 'top_products').query.filters[0].attribute).toBe('products.category')
    d = must(edit(d, { type: 'setFilter', widget: 'top_products', change: { op: 'set', index: 0, filter: filterEntry('products.category', 'not_equals', ['Apparel']) }, catalog }))
    expect(widget(d, 'top_products').query.filters[0]).toEqual({ attribute: 'products.category', operator: 'not_equals', values: ['Apparel'] })
    d = must(edit(d, { type: 'setFilter', widget: 'top_products', change: { op: 'remove', index: 0 } }))
    d = must(edit(d, { type: 'setFilter', widget: 'top_products', change: { op: 'remove', index: 0 } }))
    expect('filters' in widget(d, 'top_products').query).toBe(false)
  })
  it('refuse an operator the field\'s type does not take, with the engine\'s words, and change nothing', async () => {
    const draft = await open()
    const r = edit(draft, { type: 'setFilter', widget: 'by_region', change: { op: 'add', filter: { attribute: 'sales.net_amount', operator: 'contains', values: ['x'] } }, catalog })
    expect(r.error).toMatchObject({ code: 'bad_value', message: 'sales.net_amount is a measure, not a attribute.' })
    const typed: Catalog = { ...catalog, attributes: catalog.attributes.map((m) => (m.name === 'date.year' ? { ...m, type: 'integer' } : m)) }
    const r2 = edit(draft, { type: 'setFilter', widget: 'by_region', change: { op: 'add', filter: { attribute: 'date.year', operator: 'contains', values: ['2'] } }, catalog: typed })
    expect(r2.error).toMatchObject({ code: 'bad_value', message: "contains can't be used on integer member date.year." })
    expect(r2.draft.text).toBe(draft.text)
  })
  it('refuse a text widget, an explicit query and a position that is not there', async () => {
    const draft = await open()
    expect(edit(draft, { type: 'setFilter', widget: 'note', change: { op: 'remove', index: 0 } }).error).toMatchObject({ code: 'bad_value' })
    expect(edit(draft, { type: 'setFilter', widget: 'net', change: { op: 'remove', index: 4 } }).error).toMatchObject({ code: 'bad_position' })
  })
})

describe('the order and the sort commands', () => {
  it('add, flip, move and remove an order entry of the query', async () => {
    let d = await open()
    d = must(edit(d, { type: 'setOrder', widget: 'by_region', change: { op: 'add', field: 'customers.region' } }))
    expect(widget(d, 'by_region').query.order).toEqual([['sales.net_amount', 'desc'], ['customers.region', 'asc']])
    d = must(edit(d, { type: 'setOrder', widget: 'by_region', change: { op: 'direction', index: 1, dir: 'desc' } }))
    d = must(edit(d, { type: 'setOrder', widget: 'by_region', change: { op: 'move', from: 1, to: 0 } }))
    expect(widget(d, 'by_region').query.order).toEqual([['customers.region', 'desc'], ['sales.net_amount', 'desc']])
    d = must(edit(d, { type: 'setOrder', widget: 'by_region', change: { op: 'remove', index: 0 } }))
    d = must(edit(d, { type: 'setOrder', widget: 'by_region', change: { op: 'remove', index: 0 } }))
    expect('order' in widget(d, 'by_region').query).toBe(false)
  })
  it('write a table sort, a matrix axis sort and a chart channel sort, and take them away', async () => {
    let d = await open()
    d = must(edit(d, { type: 'setSort', widget: 'top_products', target: { kind: 'table' }, sort: { by: 'sales.units', dir: 'desc' }, catalog }))
    expect(widget(d, 'top_products').encodings.sort).toEqual({ by: 'sales.units', dir: 'desc' })
    expect(sortAt(widget(d, 'top_products'), { kind: 'table' })).toEqual({ by: 'sales.units', dir: 'desc' })
    d = must(edit(d, { type: 'setSort', widget: 'top_products', target: { kind: 'table' }, sort: undefined }))
    expect('sort' in widget(d, 'top_products').encodings).toBe(false)
    d = must(edit(d, { type: 'setSort', widget: 'by_region', target: { kind: 'channel', channel: 'x' }, sort: { by: 'field', dir: 'desc' } }))
    expect(widget(d, 'by_region').encodings.x).toEqual({ field: 'customers.region', sort: 'descending' })
    expect(sortAt(widget(d, 'by_region'), { kind: 'channel', channel: 'x' })).toEqual({ by: 'field', dir: 'desc' })
  })
  it('write a matrix axis sort where it belongs and refuse a dimension as the measure to sort by', () => {
    const doc = { type: 'matrix', encodings: { rows: ['customers.region'], values: [{ field: 'sales.net_amount' }] }, query: { model: 'retail_sales' } } as any
    expect(sortOpsOf(doc, { by: 'sales.net_amount', dir: 'desc' })).toEqual([{ op: 'set', path: ['widgets', 'm', 'encodings', 'sort'], value: { rows: { by: 'sales.net_amount', dir: 'desc' } } }])
    const entry = { ...doc, encodings: { ...doc.encodings, rows: [{ field: 'customers.region', sort: { by: 'label' } }] } }
    expect(sortOpsOf(entry, { by: 'sort_key' })).toEqual([{ op: 'set', path: ['widgets', 'm', 'encodings', 'rows', 0, 'sort'], value: { by: 'sort_key' } }])
    expect(() => sortOpsOf(doc, { by: 'customers.region' }, catalog)).toThrow('customers.region is a attribute, not a measure.')
  })
})

import { sortOps } from './queryParts'
function sortOpsOf(widgetDoc: any, sort: { by: string; dir?: 'asc' | 'desc' }, withCatalog?: Catalog) {
  return sortOps(widgetDoc, 'm', { kind: 'axis', axis: 'rows' }, sort, withCatalog)
}
