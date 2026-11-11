import { describe, expect, it } from 'vitest'
import { rows } from './fixtures'
import { chartDocument, groups, measure, nextSort, shapeRows, tableRows } from './shape'
import type { WidgetSpec } from './spec'
import { validateRenderedView } from '@dbdatakit/visuals'

const widget = (over: Partial<WidgetSpec>): WidgetSpec => ({ kind: 'bar', title: 'Quota by region', entity: 'territory', aggregate: 'sum', measure: { name: 'quota', label: null }, by: { name: 'region', label: 'Region' }, columns: [], limit: 10, ...over })

describe('a grid', () => {
  it('filters by the text an attribute contains, in any case, and leaves the input alone', () => {
    const before = rows.map((r) => r.values.code)
    expect(shapeRows(rows, { region: 'AMER' }, []).map((r) => r.values.code)).toEqual(['NA', 'LA'])
    expect(shapeRows(rows, { region: '  ' }, [])).toHaveLength(4)
    expect(rows.map((r) => r.values.code)).toEqual(before)
  })

  it('sorts by each key in turn, numbers as numbers, empty values last in either direction', () => {
    expect(shapeRows(rows, {}, [{ attribute: 'quota', direction: 'asc' }]).map((r) => r.values.code)).toEqual(['LA', 'EU', 'NA', 'AP'])
    expect(shapeRows(rows, {}, [{ attribute: 'quota', direction: 'desc' }]).map((r) => r.values.code)).toEqual(['NA', 'EU', 'LA', 'AP'])
    expect(shapeRows(rows, {}, [{ attribute: 'region', direction: 'desc' }, { attribute: 'code', direction: 'asc' }]).map((r) => r.values.code)).toEqual(['EU', 'AP', 'LA', 'NA'])
  })

  it('a click on a header goes ascending, descending, then back to the spec', () => {
    const initial = [{ attribute: 'name', direction: 'asc' as const }]
    const one = nextSort(initial, 'code', initial)
    expect(one).toEqual([{ attribute: 'code', direction: 'asc' }])
    const two = nextSort(one, 'code', initial)
    expect(two).toEqual([{ attribute: 'code', direction: 'desc' }])
    expect(nextSort(two, 'code', initial)).toBe(initial)
  })
})

describe('a dashboard', () => {
  it('measures: a count, and the sum, mean, least and greatest of the numbers there are', () => {
    expect(measure(rows, widget({ aggregate: 'count', measure: null }))).toBe(4)
    expect(measure(rows, widget({ aggregate: 'sum' }))).toBe(165)
    expect(measure(rows, widget({ aggregate: 'avg' }))).toBe(55)
    expect(measure(rows, widget({ aggregate: 'min' }))).toBe(25)
    expect(measure(rows, widget({ aggregate: 'max' }))).toBe(100)
    expect(measure([], widget({ aggregate: 'sum' }))).toBeNull()
  })

  it('groups a bar by the greatest first and a line in order, to the limit', () => {
    expect(groups(rows, widget({}))).toEqual([{ group: 'amer', value: 125 }, { group: 'emea', value: 40 }])   // apac has no number
    expect(groups(rows, widget({ kind: 'line' })).map((g) => g.group)).toEqual(['amer', 'emea'])
    expect(groups(rows, widget({ limit: 1 }))).toHaveLength(1)
  })

  it('makes a chart document the shared renderer accepts (a spec inside the subset, values inline)', () => {
    const doc = chartDocument([{ id: 'w0', widget: widget({}), rows }, { id: 'w1', widget: widget({ kind: 'line', aggregate: 'count', measure: null }), rows }])
    expect(validateRenderedView(doc)).toEqual([])
    expect(doc.views[0].vega_lite.mark).toBe('bar')
    expect(doc.views[1].vega_lite.mark).toBe('line')
  })

  it('a table shows the chosen attributes under their labels, the first rows only', () => {
    const t = tableRows(rows, widget({ kind: 'table', limit: 2, columns: [{ name: 'code', label: 'Code' }, { name: 'quota', label: null }] }))
    expect(t.columns).toEqual(['Code', 'quota'])
    expect(t.rows).toEqual([{ Code: 'EU', quota: 40 }, { Code: 'NA', quota: 100 }])
  })
})
