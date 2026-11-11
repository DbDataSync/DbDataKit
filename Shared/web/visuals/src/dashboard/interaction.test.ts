import { describe, expect, it } from 'vitest'
import { validateText } from '../client/text'
import type { ParameterDefinition, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { checkDrill, chartClick, clearedValue, coerceValue, datumFromItem, resolveDrill, rowOf, selectFrom, selectedOf, valueOf, type Hit } from './interaction'

const text = `
schemaVersion: 1
kind: dashboard
title: Desk
parameters:
  - {name: region, type: string, for: customers.region, many: true, default: [], label: Region}
  - {name: store, type: integer}
  - {name: year, type: integer, required: true, default: 2026}
  - {name: one, type: string}
widgets:
  regions: {type: control, parameter: region, control: multiselect, label: Region, values: {attribute: customers.region}}
  by_region:
    type: chart
    mark: bar
    encodings: {x: customers.region, y: sales.net_amount}
    query: {model: retail_sales}
    on_select: {set: region, from: customers.region}
    drill: {to: {dashboard: detail}, with: {region: $customers.region, year: $params.year, tag: fixed}}
  stores:
    type: table
    encodings: {columns: [{field: customers.region, label: Region}, {field: stores.store, label: Store}, {field: sales.units, label: Units}]}
    query: {model: retail_sales}
  only_drill:
    type: table
    encodings: {columns: [{field: customers.region, label: Region}]}
    query: {model: retail_sales}
    drill: {to: {report: monthly}, with: {region: $customers.region}}
  by_cell:
    type: matrix
    encodings: {rows: [customers.region, stores.store], columns: [products.category], values: [{field: sales.units}]}
    query: {model: retail_sales}
    on_select: {set: store, from: stores.store}
  single:
    type: chart
    mark: bar
    encodings: {x: customers.region, y: sales.net_amount}
    query: {model: retail_sales}
    on_select: {set: one, from: customers.region}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12}
    tiles:
      - {widget: regions, x: 0, y: 0, w: 12, h: 1}
      - {widget: by_region, x: 0, y: 1, w: 6, h: 3}
      - {widget: stores, x: 6, y: 1, w: 6, h: 3}
      - {widget: only_drill, x: 0, y: 4, w: 6, h: 3}
      - {widget: by_cell, x: 6, y: 4, w: 6, h: 3}
      - {widget: single, x: 0, y: 7, w: 12, h: 2}
`
const parsed = validateText(text)
const doc = parsed.document as ViewsDoc
const w = (id: string) => doc.widgets[id]

const table: RenderedWidget = {
  type: 'table',
  columns: [{ field: 'customers.region', label: 'Region' }, { field: 'stores.store', label: 'Store' }, { field: 'sales.units', label: 'Units' }],
  rows: [
    { cells: [{ v: 'West', text: 'West' }, { v: 12, text: '12' }, { v: 90, text: '90' }] },
    { cells: [{ text: 'East' }, { v: 7, text: '7' }, { v: 70, text: '70' }] },
    { total: true, cells: [{ text: 'Total' }, { text: '' }, { v: 160, text: '160' }] },
  ],
}
const matrix: RenderedWidget = {
  id: 'by_cell',
  type: 'matrix',
  columns: [{ labels: ['Apparel'] }, { labels: ['Footwear'] }, { labels: ['All'], total: true }],
  rows: [
    { labels: ['West', '12'], cells: [{ text: '1' }, { text: '2' }, { text: '3' }] },
    { labels: ['Total', ''], total: true, cells: [{ text: '1' }, { text: '2' }, { text: '3' }] },
  ],
}

describe('the sample document is valid', () => {
  it('has no error', () => expect(parsed.diagnostics.filter((d) => d.severity === 'error')).toEqual([]))
})

describe('rowOf', () => {
  it('a chart datum is its own row, read by the model name or the chart spelling', () => {
    const row = rowOf(w('by_region'), { type: 'chart', id: 'x', vega_lite: { data: { values: [] } } }, { kind: 'datum', datum: { customers_region: 'West', sales_net_amount: 5 } })!
    expect(valueOf(row, 'customers.region')).toBe('West')
    expect(valueOf(row, 'customers_region')).toBe('West')
    expect(valueOf(row, 'nothing')).toBeUndefined()
  })
  it('a table row is its columns by field: the cell value, else its text; a total row is nothing', () => {
    expect(rowOf(w('stores'), table, { kind: 'row', index: 0 })).toEqual({ 'customers.region': 'West', 'stores.store': 12, 'sales.units': 90 })
    expect(rowOf(w('stores'), table, { kind: 'row', index: 1 })).toEqual({ 'customers.region': 'East', 'stores.store': 7, 'sales.units': 70 })
    expect(rowOf(w('stores'), table, { kind: 'row', index: 2 })).toBeNull()
    expect(rowOf(w('stores'), table, { kind: 'row', index: 9 })).toBeNull()
  })
  it('a matrix row label is the row fields, a cell adds the column fields, a total is nothing', () => {
    expect(rowOf(w('by_cell'), matrix, { kind: 'matrix', row: 0 })).toEqual({ 'customers.region': 'West', 'stores.store': '12' })
    expect(rowOf(w('by_cell'), matrix, { kind: 'matrix', row: 0, column: 1 })).toEqual({ 'customers.region': 'West', 'stores.store': '12', 'products.category': 'Footwear' })
    expect(rowOf(w('by_cell'), matrix, { kind: 'matrix', row: 0, column: 2 })).toBeNull()
    expect(rowOf(w('by_cell'), matrix, { kind: 'matrix', row: 1 })).toBeNull()
  })
  it('a hit that does not fit the widget, or a widget with no result, is nothing', () => {
    expect(rowOf(w('stores'), table, { kind: 'datum', datum: { a: 1 } })).toBeNull()
    expect(rowOf(w('by_region'), table, { kind: 'row', index: 0 })).toBeNull()
    expect(rowOf(w('stores'), undefined, { kind: 'row', index: 0 })).toBeNull()
  })
})

describe('selectFrom (cross-filter)', () => {
  const west: Hit = { kind: 'datum', datum: { customers_region: 'West' } }
  const row = (hit: Hit = west) => rowOf(w('by_region'), { type: 'chart', id: 'x', vega_lite: { data: { values: [] } } }, hit)

  it('sets the declared parameter, as a list of one for a `many` parameter', () => {
    expect(selectFrom(doc, 'by_region', row(), { region: [] })).toEqual({ ok: true, parameter: 'region', value: ['West'], cleared: false })
    expect(selectFrom(doc, 'by_region', row(), { region: ['East'] })).toEqual({ ok: true, parameter: 'region', value: ['West'], cleared: false })
  })
  it('selecting the one value again clears the selection, back to the default', () => {
    expect(selectFrom(doc, 'by_region', row(), { region: ['West'] })).toEqual({ ok: true, parameter: 'region', value: [], cleared: true })
    expect(selectFrom(doc, 'single', row(), { one: 'West' })).toEqual({ ok: true, parameter: 'one', value: null, cleared: true })
  })
  it('an additive selection adds a value to the list and takes it out again', () => {
    const east = row({ kind: 'datum', datum: { customers_region: 'East' } })
    expect(selectFrom(doc, 'by_region', east, { region: ['West'] }, { additive: true })).toMatchObject({ value: ['West', 'East'], cleared: false })
    expect(selectFrom(doc, 'by_region', east, { region: ['West', 'East'] }, { additive: true })).toMatchObject({ value: ['West'], cleared: false })
    expect(selectFrom(doc, 'by_region', east, { region: ['East'] }, { additive: true })).toMatchObject({ value: [], cleared: true })
  })
  it('reads the value as the parameter\'s type, and refuses one that is not', () => {
    expect(selectFrom(doc, 'by_cell', rowOf(w('by_cell'), matrix, { kind: 'matrix', row: 0 }), { store: null })).toEqual({ ok: true, parameter: 'store', value: 12, cleared: false })
    expect(selectFrom(doc, 'by_cell', { 'stores.store': 'abc' }, { store: null })).toMatchObject({ ok: false, code: 'bad_value' })
    expect(selectFrom(doc, 'by_cell', { 'stores.store': '7' }, { store: null })).toMatchObject({ ok: true, value: 7 })
    expect(selectFrom(doc, 'by_cell', { 'stores.store': '12' }, { store: 12 })).toMatchObject({ ok: true, value: null, cleared: true })
  })
  it('refuses a row with no value for `from`, a widget with no on_select, and nothing pointed at', () => {
    expect(selectFrom(doc, 'by_region', { customers_region: null }, { region: [] })).toMatchObject({ ok: false, code: 'no_value' })
    expect(selectFrom(doc, 'by_region', { other: 'x' }, { region: [] })).toMatchObject({ ok: false, code: 'no_value' })
    expect(selectFrom(doc, 'by_region', null, { region: [] })).toMatchObject({ ok: false, code: 'no_value' })
    expect(selectFrom(doc, 'stores', { 'customers.region': 'West' }, { region: [] })).toMatchObject({ ok: false, code: 'no_on_select' })
  })
  it('a matrix selects from its row labels', () => {
    expect(selectFrom(doc, 'by_cell', rowOf(w('by_cell'), matrix, { kind: 'matrix', row: 0, column: 0 }), {})).toMatchObject({ ok: true, parameter: 'store', value: 12 })
  })
  it('clearedValue is the default, and what a widget has chosen is read from the values', () => {
    expect(clearedValue(doc, 'region')).toEqual([])
    expect(clearedValue(doc, 'year')).toBe(2026)
    expect(clearedValue(doc, 'one')).toBeNull()
    expect(selectedOf(w('by_region'), { region: ['West', 'East'] })).toEqual(['West', 'East'])
    expect(selectedOf(w('single'), { one: 'West' })).toEqual(['West'])
    expect(selectedOf(w('single'), { one: null })).toEqual([])
    expect(selectedOf(w('only_drill'), { region: ['West'] })).toEqual([])
  })
  it('coerceValue', () => {
    const def = (type: ParameterDefinition['type']) => ({ type })
    expect(coerceValue(def('integer'), 3.5)).toBeUndefined()
    expect(coerceValue(def('integer'), '3')).toBe(3)
    expect(coerceValue(def('number'), '3.5')).toBe(3.5)
    expect(coerceValue(def('number'), '')).toBeUndefined()
    expect(coerceValue(def('boolean'), 'true')).toBe(true)
    expect(coerceValue(def('boolean'), 'yes')).toBeUndefined()
    expect(coerceValue(def('string'), 5)).toBe('5')
    expect(coerceValue(def('string'), null)).toBeUndefined()
  })
})

describe('resolveDrill and checkDrill', () => {
  const west = rowOf(w('by_region'), { type: 'chart', id: 'x', vega_lite: { data: { values: [] } } }, { kind: 'datum', datum: { customers_region: 'West' } })

  it('passes `$field` of the clicked row, `$params.name` and literals', () => {
    expect(resolveDrill(doc, 'by_region', west, { region: [], year: 2025 })).toEqual({
      ok: true,
      drill: { kind: 'dashboard', name: 'detail', parameters: { region: 'West', year: 2025, tag: 'fixed' } },
    })
    expect(resolveDrill(doc, 'only_drill', { 'customers.region': 'East' }, {})).toEqual({ ok: true, drill: { kind: 'report', name: 'monthly', parameters: { region: 'East' } } })
  })
  it('refuses a row with no value for a field it passes, naming the field and the parameter', () => {
    const out = resolveDrill(doc, 'by_region', { sales_net_amount: 1 }, { year: 1 })
    expect(out).toMatchObject({ ok: false, code: 'no_value' })
    expect((out as { message: string }).message).toContain('`customers.region`')
    expect((out as { message: string }).message).toContain('`region`')
    expect(resolveDrill(doc, 'by_region', null, {})).toMatchObject({ ok: false, code: 'no_value' })
  })
  it('refuses a widget with no drill', () => expect(resolveDrill(doc, 'stores', {}, {})).toMatchObject({ ok: false, code: 'no_drill' }))

  const target: ParameterDefinition[] = [{ name: 'region', type: 'string', required: true }, { name: 'year', type: 'integer', required: true, default: 2026 }, { name: 'tag', type: 'string' }]
  it('passes a drill that gives every required parameter of the target', () => {
    const drill = (resolveDrill(doc, 'by_region', west, { year: 2025 }) as { drill: Parameters<typeof checkDrill>[0] }).drill
    expect(checkDrill(drill, target)).toEqual({ ok: true })
  })
  it('refuses a missing required parameter naming it (a default is not required; an empty value is missing)', () => {
    const base = { kind: 'dashboard' as const, name: 'detail' }
    expect(checkDrill({ ...base, parameters: {} }, target)).toMatchObject({ ok: false, missing: ['region'], unknown: [] })
    expect(checkDrill({ ...base, parameters: { region: null } }, target)).toMatchObject({ ok: false, missing: ['region'] })
    expect(checkDrill({ ...base, parameters: { region: [] } }, target)).toMatchObject({ ok: false, missing: ['region'] })
    const out = checkDrill({ ...base, parameters: {} }, target) as { message: string }
    expect(out.message).toBe('The drill to dashboard `detail` does not give the required parameter `region`.')
  })
  it('refuses a parameter the target does not declare, and says nothing when the target is not known', () => {
    const out = checkDrill({ kind: 'dashboard', name: 'detail', parameters: { region: 'West', colour: 'red' } }, target)
    expect(out).toMatchObject({ ok: false, missing: [], unknown: ['colour'] })
    expect((out as { message: string }).message).toContain('`colour`')
    expect(checkDrill({ kind: 'view', name: 'v', parameters: { anything: 1 } }, undefined)).toEqual({ ok: true })
  })
})

describe('chart clicks', () => {
  it('a Vega item\'s datum is a row of scalars; a click on nothing is not a hit', () => {
    expect(datumFromItem({ datum: { customers_region: 'West', n: 3, ok: true, none: null, nested: { a: 1 }, list: [1] } })).toEqual({ customers_region: 'West', n: 3, ok: true, none: null })
    expect(datumFromItem(null)).toBeNull()
    expect(datumFromItem({})).toBeNull()
    expect(datumFromItem({ datum: {} })).toBeNull()
    expect(datumFromItem({ datum: [1] })).toBeNull()
  })
  it('the listener reports the datum and the modifiers of the event', () => {
    const hits: Array<[Hit, unknown]> = []
    const listener = chartClick((hit, how) => hits.push([hit, how]))
    listener({ shiftKey: true }, { datum: { a: 'x' } })
    listener({ altKey: true }, { datum: { a: 'y' } })
    listener({}, null)
    listener(undefined, { datum: { a: 'z' } })
    expect(hits).toEqual([
      [{ kind: 'datum', datum: { a: 'x' } }, { additive: true, drill: false }],
      [{ kind: 'datum', datum: { a: 'y' } }, { additive: false, drill: true }],
      [{ kind: 'datum', datum: { a: 'z' } }, { additive: false, drill: false }],
    ])
  })
})
