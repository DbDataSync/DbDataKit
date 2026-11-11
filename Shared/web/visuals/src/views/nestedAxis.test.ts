import { describe, expect, it } from 'vitest'
import { validateVegaLiteSubset } from '../validate'
import { applyNestedAxis } from './nestedAxis'

const spec = (mark: unknown = 'bar', column: unknown = { field: 'orders.year', type: 'ordinal' }) => ({
  mark,
  encoding: { x: { field: 'orders.month', type: 'ordinal' }, y: { field: 'sales.net', type: 'quantitative' }, ...(column === null ? {} : { column }) },
  data: { values: [{ 'orders.year': 2025, 'orders.month': 'Jan', 'sales.net': 1 }] },
})

describe('applyNestedAxis (DBR42, client half)', () => {
  it('turns the column facet into group headers below the inner labels, with no space between groups, and leaves the input alone', () => {
    const input = spec()
    const out = applyNestedAxis(input, true) as any
    expect(out.spacing).toBe(0)
    expect(out.resolve).toEqual({ scale: { x: 'independent' } })
    expect(out.encoding.column.header).toMatchObject({ orient: 'bottom', labelOrient: 'bottom', titleOrient: 'bottom' })
    expect(out.encoding.x).toEqual(input.encoding.x)
    expect(input).toEqual(spec())
    expect(out.data).toEqual(input.data)
  })

  it('does nothing without the flag, without a column, for a line or an area, or when the header is off', () => {
    const plain = spec()
    expect(applyNestedAxis(plain, undefined)).toBe(plain)
    const none = spec('bar', null)
    expect(applyNestedAxis(none, true)).toBe(none)
    for (const mark of ['line', 'area']) { const s = spec(mark); expect(applyNestedAxis(s, true)).toBe(s) }
    const off = spec('bar', { field: 'orders.year', type: 'ordinal', header: null })
    expect(applyNestedAxis(off, true)).toBe(off)
  })

  it('works for the object form of a mark and for point', () => {
    expect((applyNestedAxis(spec({ type: 'bar' }), true) as any).spacing).toBe(0)
    expect((applyNestedAxis(spec('point'), true) as any).spacing).toBe(0)
  })

  it('what it starts from is inside the subset (the keys it adds are the renderer\'s, added after the check)', () => {
    const { data: _data, ...withoutData } = spec()
    expect(validateVegaLiteSubset(withoutData, '$')).toEqual([])
  })
})
