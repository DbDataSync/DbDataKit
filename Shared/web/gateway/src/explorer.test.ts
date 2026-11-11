import { validateRenderedView } from '@dbdatakit/visuals'
import { describe, expect, it } from 'vitest'
import { bodyFor, chartOf, encodingProblem, matrixOf, members, picksOf, pinRequest, refusal, requestFromPicks, requestText, roles, scansOf, sqlFromPicks, suggest, toggle, viewDocument } from './explorer'
import type { RunResult } from './runTypes'
import { view } from './modelBrowserFixture'

const picks = { attributes: ['customer.region'], measures: ['revenue'], limit: 50 }

const result = (over: Partial<RunResult> = {}): RunResult => ({
  status: 'ok', warnings: [], request_hash: 'abc',
  columns: [{ name: 'customer.region', type: 'string' }, { name: 'quarter', type: 'string' }, { name: 'revenue', type: 'number' }],
  rows: [['East', 'Q1', 10], ['East', 'Q2', 20], ['West', 'Q1', 5], ['West', 'Q2', 15]],
  ...over,
})

describe('the palette and the typed request', () => {
  it('builds the request a person would type, and reads a typed one back into the same picks', () => {
    const typed = requestFromPicks(picks)
    expect(typed).toEqual({ spec_version: '0.3', kind: 'query', query: { attributes: ['customer.region'], measures: ['revenue'], limit: 50 } })
    expect(JSON.parse(requestText(picks))).toEqual(typed)
    expect(picksOf(requestText(picks))).toEqual(picks)
  })
  it('does not pretend to show a request the palette cannot', () => {
    expect(picksOf('{"query": {"measures": ["revenue"], "filters": [{}]}}')).toBeNull()
    expect(picksOf('SELECT 1')).toBeNull()
  })
  it('writes the same picks as semantic SQL, keeps the order picked and clamps the limit', () => {
    expect(sqlFromPicks('shop_sales', picks)).toBe('SELECT customer.region, MEASURE(revenue) FROM shop_sales GROUP BY ALL LIMIT 50')
    expect(sqlFromPicks('shop_sales', { attributes: [], measures: ['revenue'], limit: 99999 })).toBe('SELECT MEASURE(revenue) FROM shop_sales LIMIT 10000')
    expect(toggle(toggle(picks, 'measures', 'margin_pct'), 'attributes', 'customer.region').attributes).toEqual([])
    expect(toggle(picks, 'measures', 'margin_pct').measures).toEqual(['revenue', 'margin_pct'])
  })
  it('lists the catalog\'s members for the palette', () => {
    expect(members(view)).toEqual({ attributes: ['customer.region', 'customer.name'], measures: ['revenue', 'margin_pct'] })
  })
})

describe('what is sent', () => {
  it('sends JSON as a request and anything else as semantic SQL, with the explain flag and the limit', () => {
    expect(bodyFor('m', '{"kind":"query","query":{}}', { explain: true, limit: 5 })).toEqual({ body: { model: 'm', explain: true, limit: 5, request: { kind: 'query', query: {} } } })
    expect(bodyFor('m', 'SELECT 1;')).toEqual({ body: { model: 'm', sql: 'SELECT 1' } })
  })
  it('says what is wrong before sending', () => {
    expect(bodyFor('m', '  ')).toHaveProperty('problem')
    expect((bodyFor('m', '{"a": ') as { problem: string }).problem).toContain('not valid JSON')
  })
  it('puts the engine\'s code in front of its message once', () => {
    expect(refusal('The model has no measure x.', 'unknown_measure')).toBe('unknown_measure: The model has no measure x.')
    expect(refusal('unknown_measure at $: nope', 'unknown_measure')).toBe('unknown_measure at $: nope')
  })
  it('hands the pin panel the query body', () => {
    expect(JSON.parse(pinRequest(requestText(picks), picks)!)).toEqual({ attributes: ['customer.region'], measures: ['revenue'], limit: 50 })
    expect(JSON.parse(pinRequest('SELECT 1', picks)!).measures).toEqual(['revenue'])
    expect(pinRequest('SELECT 1', { attributes: [], measures: [], limit: 1 })).toBeNull()
  })
  it('reads an explain\'s rows as scans', () => {
    const plan: RunResult = { status: 'explained', warnings: [], request_hash: null, columns: [{ name: 'source', type: 'string' }, { name: 'engine', type: 'string' }, { name: 'text', type: 'string' }], rows: [['shop', 'postgres', 'SELECT 1']] }
    expect(scansOf(plan)).toEqual([{ source: 'shop', engine: 'postgres', text: 'SELECT 1' }])
  })
})

describe('views of a result', () => {
  it('suggests the first attribute along, the second across and the first measure as the value', () => {
    expect(suggest(result())).toEqual({ rows: ['customer.region'], columns: ['quarter'], value: 'revenue' })
    expect(roles(result()).measures).toEqual(['revenue'])
  })
  it('draws a matrix with the headings of both attributes and the server\'s numbers in the cells', () => {
    const m = matrixOf(result(), suggest(result()))
    expect(m.rows.map((r) => r.labels)).toEqual([['East'], ['West']])
    expect(m.columns.map((c) => c.labels)).toEqual([['Q1'], ['Q2']])
    expect(m.rows[0]!.cells.map((c) => c.v)).toEqual([10, 20])
    expect(validateRenderedView({ kind: 'rendered_view', status: 'complete', views: [m] })).toEqual([])
  })
  it('reads totals from the result: the rows the rollup column marks, as the total row and the total column, never added up here', () => {
    const withTotals = result({
      columns: [{ name: 'customer.region', type: 'string' }, { name: 'quarter', type: 'string' }, { name: 'revenue', type: 'number' }, { name: '_rollup_level', type: 'integer' }],
      rows: [['East', 'Q1', 10, 0], ['West', 'Q1', 5, 0], [null, 'Q1', 999, 1], ['East', null, 777, 1], [null, null, 5000, 2]],
    })
    expect(roles(withTotals).hasTotals).toBe(true)
    const m = matrixOf(withTotals, suggest(withTotals))
    expect(m.rows.map((r) => [r.labels[0], r.total ?? false])).toEqual([['East', false], ['West', false], ['Total', true]])
    expect(m.columns.map((c) => [c.labels[0], c.total ?? false])).toEqual([['Q1', false], ['Total', true]])
    const total = m.rows[2]!
    expect(total.cells.map((c) => c.v)).toEqual([999, 5000])
    expect(validateRenderedView({ kind: 'rendered_view', status: 'complete', views: [m] })).toEqual([])
  })
  it('draws a bar chart inside the closed subset, leaving a total out of the bars', () => {
    const withTotals = result({ columns: [{ name: 'customer.region', type: 'string' }, { name: 'revenue', type: 'number' }, { name: '_rollup_level', type: 'integer' }], rows: [['East', 10, 0], ['West', 5, 0], [null, 15, 1]] })
    const e = suggest(withTotals)
    const chart = chartOf(withTotals, 'bar', e)
    expect(chart.vega_lite.data.values).toEqual([{ 'customer.region': 'East', revenue: 10 }, { 'customer.region': 'West', revenue: 5 }])
    expect(validateRenderedView({ kind: 'rendered_view', status: 'complete', views: [chart] })).toEqual([])
    expect(viewDocument(withTotals, 'table', e)).toBeNull()
  })
  it('refuses an encoding the result cannot carry, in words with a code', () => {
    const r = result()
    expect(encodingProblem(r, 'bar', { rows: ['nope'], columns: [], value: 'revenue' })).toContain("view_unknown_field: 'nope'")
    expect(encodingProblem(r, 'bar', { rows: ['customer.region'], columns: [], value: 'quarter' })).toContain('view_value_not_numeric')
    expect(encodingProblem(r, 'line', { rows: [], columns: [], value: 'revenue' })).toContain('view_missing_rows')
    expect(encodingProblem(r, 'bar', suggest(r))).toBeNull()
  })
  it('lets the renderer refuse a chart that tries to compute or fetch', () => {
    const chart = chartOf(result(), 'bar', suggest(result()))
    const sneaky = { ...chart, vega_lite: { ...chart.vega_lite, transform: [{ calculate: '1', as: 'x' }] } }
    expect(validateRenderedView({ kind: 'rendered_view', status: 'complete', views: [sneaky] }).map((i) => i.code)).toContain('vega_disallowed')
  })
})
