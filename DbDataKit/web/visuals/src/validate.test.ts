import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { semanticSchemaPath, viewFixtures } from './testing/fixtures'
import { validateRenderedView, validateViewDocument, validateVegaLiteSubset } from './validate'

const doc = (vega_lite: unknown) => ({ spec_version: '0.3', kind: 'view', result: { ref: 'result.x' }, views: [{ type: 'chart', id: 'c1', vega_lite }] })
const allowed = {
  $schema: 'https://vega.github.io/schema/vega-lite/v5.json',
  mark: 'bar',
  title: 'Revenue by region',
  encoding: {
    x: { field: 'customers.region', type: 'nominal', sort: 'sort_key', title: 'Region' },
    y: { field: 'orders.net_revenue', type: 'quantitative', stack: null },
    color: { field: 'date.fiscal_quarter', type: 'ordinal' },
    tooltip: [{ field: 'orders.net_revenue', type: 'quantitative' }],
  },
}
const codes = (issues: { code: string }[]) => issues.map((i) => i.code)

describe('the schema is the semantic library\'s own', () => {
  it('is byte for byte the schema the library embeds', () => {
    expect(readFileSync(join(import.meta.dirname, '../schemas/view.schema.json'), 'utf8')).toBe(readFileSync(semanticSchemaPath, 'utf8'))
  })
})

describe('a chart spec inside the Vega-Lite subset', () => {
  it('passes', () => {
    expect(validateViewDocument(doc(allowed))).toEqual([])
  })

  it('passes with a mark object, an explicit sort list and a stack', () => {
    expect(validateVegaLiteSubset({ mark: { type: 'line', point: true }, encoding: { x: { field: 'a', type: 'ordinal', sort: ['q1', 'q2'] }, y: { field: 'b', type: 'quantitative', stack: 'zero' } } })).toEqual([])
  })
})

describe('a chart spec that could fetch or compute', () => {
  const refused: Array<[string, unknown]> = [
    ['a URL for data', { ...allowed, data: { url: 'https://example.com/x.csv' } }],
    ['a URL for data, scheme-relative', { ...allowed, data: { url: '//example.com/x.csv' } }],
    ['a URL for data, a file', { ...allowed, data: { url: 'file:///etc/passwd' } }],
    ['a named dataset', { ...allowed, data: { name: 'x' }, datasets: { x: [{ a: 1 }] } }],
    ['a URL hidden in a nested key', { ...allowed, encoding: { ...allowed.encoding, x: { field: 'a', type: 'nominal', condition: { url: 'https://example.com' } } } }],
    ['a transform with an expression', { ...allowed, transform: [{ calculate: 'datum.a * 2', as: 'b' }] }],
    ['a filter expression', { ...allowed, transform: [{ filter: 'datum.a > 1' }] }],
    ['an aggregate', { ...allowed, encoding: { ...allowed.encoding, y: { field: 'a', type: 'quantitative', aggregate: 'sum' } } }],
    ['a bin', { ...allowed, encoding: { ...allowed.encoding, x: { field: 'a', type: 'quantitative', bin: true } } }],
    ['a time unit', { ...allowed, encoding: { ...allowed.encoding, x: { field: 'a', type: 'temporal', timeUnit: 'month' } } }],
    ['a parameter with an expression', { ...allowed, params: [{ name: 'p', expr: 'alert(1)' }] }],
    ['a selection', { ...allowed, selection: { s: { type: 'single' } } }],
    ['a layer', { layer: [allowed] }],
    ['a facet, repeat or concat', { facet: { field: 'a' }, spec: allowed }],
    ['a mark colour that is an expression', { ...allowed, mark: { type: 'bar', color: { expr: 'alert(1)' } } }],
    ['a value channel', { ...allowed, encoding: { ...allowed.encoding, color: { value: 'red' } } }],
    ['a condition', { ...allowed, encoding: { ...allowed.encoding, color: { field: 'a', condition: { test: 'datum.a > 1', value: 'red' } } } }],
    ['a sort that aggregates', { ...allowed, encoding: { ...allowed.encoding, x: { field: 'a', type: 'nominal', sort: { op: 'sum', field: 'b' } } } }],
    ['a title that is an object', { ...allowed, title: { text: 'x', subtitle: 'y' } }],
    ['an unknown mark', { ...allowed, mark: 'arc' }],
    ['an unknown channel', { ...allowed, encoding: { ...allowed.encoding, size: { field: 'a' } } }],
    ['an unknown top-level key', { ...allowed, usermeta: {} }],
    ['an unknown type', { ...allowed, encoding: { x: { field: 'a', type: 'geojson' } } }],
    ['a $schema that points somewhere else', { ...allowed, $schema: 'https://evil.example/schema.json' }],
    ['a tooltip list with a computing member', { ...allowed, encoding: { tooltip: [{ field: 'a', aggregate: 'sum' }] } }],
  ]

  it.each(refused)('is refused: %s', (_name, spec) => {
    const issues = validateViewDocument(doc(spec))
    expect(issues.length).toBeGreaterThan(0)
    expect(new Set(codes(issues))).toEqual(new Set(['vega_disallowed']))
    expect(issues.every((i) => i.path.startsWith('$.views[0].vega_lite'))).toBe(true)
  })

  it('names where the offence is', () => {
    const issues = validateViewDocument(doc({ ...allowed, data: { url: 'https://example.com/x.csv' } }))
    expect(issues).toEqual([expect.objectContaining({ code: 'vega_disallowed', path: '$.views[0].vega_lite.data' })])
  })

  it('is refused when it is no object', () => {
    expect(codes(validateVegaLiteSubset('mark'))).toEqual(['vega_disallowed'])
    expect(validateViewDocument(doc('bar')).length).toBeGreaterThan(0)
  })
})

describe('a view document', () => {
  it('that does not match the schema is reported for that alone', () => {
    const issues = validateViewDocument({ spec_version: '0.3', kind: 'view', result: { ref: 'r' }, views: [{ type: 'matrix', id: 'm1' }, { type: 'chart', id: 'c1', vega_lite: { data: { url: 'x' } } }] })
    expect(new Set(codes(issues))).toEqual(new Set(['schema_invalid']))
  })

  it('refuses two views with one id', () => {
    const matrix = { type: 'matrix', id: 'm1', values: [{ field: 'a' }] }
    expect(validateViewDocument({ spec_version: '0.3', kind: 'view', result: { ref: 'r' }, views: [matrix, matrix] })).toEqual([expect.objectContaining({ code: 'duplicate_view_id', path: '$.views[1].id' })])
  })
})

describe('the view fixtures', () => {
  // What the document alone can decide; what needs the result (an unknown field, a stack of a measure that cannot be added, ...) is the server's.
  const documentLevel = ['schema_invalid', 'vega_disallowed', 'duplicate_view_id']

  it.each(viewFixtures().map((f) => [f.name, f] as const))('%s: the document gets the verdict the server gives it', (_name, fixture) => {
    const expected = (fixture.expect.errors ?? fixture.expect.errors_include ?? []).filter((c) => documentLevel.includes(c))
    expect(codes(validateViewDocument(fixture.view)).sort()).toEqual([...expected].sort())
  })

  it.each(viewFixtures().map((f) => [f.name, f] as const))('%s: what the server rendered is accepted by the renderer', (_name, fixture) => {
    expect(validateRenderedView(fixture.rendered)).toEqual([])
  })
})

describe('a rendered view', () => {
  const rendered = (vega_lite: unknown) => ({ kind: 'rendered_view', status: 'complete', views: [{ id: 'c1', type: 'chart', vega_lite }] })
  const withData = { mark: 'line', encoding: { x: { field: 'a', type: 'ordinal' } }, data: { values: [{ a: 'q1' }, { a: null }] } }

  it('with inline data passes', () => {
    expect(validateRenderedView(rendered(withData))).toEqual([])
  })

  it.each<[string, unknown]>([
    ['data from a URL', { ...withData, data: { url: 'https://example.com/x.csv' } }],
    ['data by name', { ...withData, data: { name: 'x' } }],
    ['inline data with a format', { ...withData, data: { values: [], format: { type: 'csv' } } }],
    ['a value that is an object', { ...withData, data: { values: [{ a: { b: 1 } }] } }],
    ['a transform', { ...withData, transform: [{ calculate: '1', as: 'b' }] }],
    ['no data', { mark: 'line', encoding: {} }],
  ])('with %s is refused', (_name, spec) => {
    expect(validateRenderedView(rendered(spec)).length).toBeGreaterThan(0)
  })

  it('that is not a rendered view is refused', () => {
    expect(codes(validateRenderedView({ kind: 'view' }))).toContain('rendered_invalid')
    expect(codes(validateRenderedView(null))).toContain('rendered_invalid')
  })
})
