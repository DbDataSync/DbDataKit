import { validateViewDocument } from '@dbdatakit/visuals'
import { describe, expect, it } from 'vitest'
import { bodyFor, members, missingSlots, picksOf, pinRequest, refusal, requestFromPicks, requestText, roles, scansOf, sqlFromPicks, suggest, toggle, totalsOf, viewSpecFor } from './explorer'
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
    expect(refusal('The encoding is wrong.', 'schema_invalid', '$.views[0].values')).toBe('schema_invalid at $.views[0].values: The encoding is wrong.')
    expect(refusal('schema_invalid at $.views[0]: nope', 'schema_invalid', '$.views[0]')).toBe('schema_invalid at $.views[0]: nope')
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
  it('names the required slots still empty and has no other rule about a view (D38)', () => {
    const none = { rows: [], columns: [], value: '' }
    expect(missingSlots('table', none)).toEqual([])
    expect(missingSlots('matrix', none)).toEqual(['Value'])
    expect(missingSlots('matrix', { ...none, value: 'revenue' })).toEqual([])
    expect(missingSlots('bar', none)).toEqual(['Along', 'Value'])
    expect(missingSlots('line', { rows: ['quarter'], columns: [], value: '' })).toEqual(['Value'])
    expect(missingSlots('line', { rows: ['quarter'], columns: [], value: 'revenue' })).toEqual([])
  })
  it('asks the Gateway for a matrix by what is chosen and never carries a cell', () => {
    const spec = viewSpecFor(result(), 'matrix', suggest(result()))!
    expect(spec).toEqual({
      spec_version: '0.3', kind: 'view', result: { ref: 'result' },
      views: [{ type: 'matrix', id: 'result', rows: ['customer.region'], columns: ['quarter'], values: [{ field: 'revenue' }] }],
    })
    expect(validateViewDocument(spec)).toEqual([])
    expect(JSON.stringify(spec)).not.toContain('"East"')
  })
  it('asks for the totals the result has and no others: a total row for the row attributes, a total column for the column attributes', () => {
    const columns = [{ name: 'customer.region', type: 'string', binding: 'customer.region' }, { name: 'quarter', type: 'string', binding: 'date.quarter' }, { name: 'revenue', type: 'number' }, { name: '_rollup_level', type: 'integer' }]
    const levels = (rolled: string[][]) => ({ level_field: '_rollup_level', levels: [{ level: 0, rolled_up: [] }, ...rolled.map((r, i) => ({ level: i + 1, rolled_up: r }))] })
    const e = { rows: ['customer.region'], columns: ['quarter'], value: 'revenue' }
    expect(roles(result({ columns })).hasTotals).toBe(true)
    // the cube: both
    expect(totalsOf(result({ columns, totals: levels([['date.quarter'], ['customer.region'], ['customer.region', 'date.quarter']]) }), e)).toEqual({ rows: true, columns: true })
    // only the columns' attribute rolled up: a total column, not a row
    expect(totalsOf(result({ columns, totals: levels([['date.quarter']]) }), e)).toEqual({ columns: true })
    expect(viewSpecFor(result({ columns, totals: levels([['customer.region']]) }), 'matrix', e)!.views[0]!.totals).toEqual({ rows: true })
    // none: nothing is asked, so the renderer is not made to refuse
    expect(totalsOf(result(), e)).toBeUndefined()
    expect(viewSpecFor(result(), 'matrix', e)!.views[0]).not.toHaveProperty('totals')
  })
  it('asks for a bar or line chart in the closed subset, the value along the first row attribute and coloured by the first column attribute', () => {
    const spec = viewSpecFor(result(), 'line', suggest(result()))!
    expect(spec.views[0]).toEqual({
      type: 'chart', id: 'result',
      vega_lite: { mark: 'line', encoding: { x: { field: 'customer.region', type: 'nominal' }, y: { field: 'revenue', type: 'quantitative' }, color: { field: 'quarter', type: 'nominal' } } },
    })
    expect(validateViewDocument(spec)).toEqual([])
    expect((viewSpecFor(result(), 'bar', { rows: ['customer.region'], columns: [], value: 'revenue' })!.views[0]!.vega_lite as { encoding: object }).encoding).not.toHaveProperty('color')
  })
  it('draws a table itself, and leaves a choice the result cannot carry to the renderer\'s refusal', () => {
    expect(viewSpecFor(result(), 'table', suggest(result()))).toBeNull()
    // an unknown field and an empty value are sent as they are: the Gateway answers in the engine\'s words
    expect(viewSpecFor(result(), 'bar', { rows: ['nope'], columns: [], value: 'revenue' })!.views[0]!.vega_lite).toMatchObject({ encoding: { x: { field: 'nope' } } })
    expect(viewSpecFor(result(), 'matrix', { rows: ['customer.region'], columns: [], value: '' })!.views[0]).toMatchObject({ values: [] })
  })
})
