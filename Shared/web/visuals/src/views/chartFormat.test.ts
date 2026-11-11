import { describe, expect, it } from 'vitest'
import { D3_FORMAT, applyChartFormats } from './chartFormat'

const spec = () => ({
  mark: 'bar',
  encoding: {
    x: { field: 'month', type: 'ordinal' },
    y: { field: 'revenue', type: 'quantitative' },
    color: { field: 'revenue', type: 'quantitative' },
    tooltip: [{ field: 'month', type: 'ordinal' }, { field: 'revenue', type: 'quantitative' }],
  },
  data: { values: [] },
})

describe('applyChartFormats', () => {
  it('gives each channel its d3 string as the axis, legend or tooltip format', () => {
    const out = applyChartFormats(spec(), { y: '$,.0f', color: '.2~s', tooltip: [null, '.0%'] }) as any
    expect(out.encoding.y.axis).toEqual({ format: '$,.0f' })
    expect(out.encoding.color.legend).toEqual({ format: '.2~s' })
    expect(out.encoding.tooltip[0].format).toBeUndefined()
    expect(out.encoding.tooltip[1].format).toBe('.0%')
    expect(out.encoding.x.axis).toBeUndefined()
  })

  it('keeps the other settings of an axis, and an axis or a legend that is off stays off', () => {
    const s = spec() as any
    s.encoding.y.axis = { title: 'Revenue' }
    s.encoding.color.legend = null
    const out = applyChartFormats(s, { y: ',.2f', color: '.0%' }) as any
    expect(out.encoding.y.axis).toEqual({ title: 'Revenue', format: ',.2f' })
    expect(out.encoding.color.legend).toBeNull()
  })

  it('uses only a string of the one pattern: nothing else reaches the spec', () => {
    const out = applyChartFormats(spec(), { y: 'datum.x', x: '${x}', tooltip: ['%Y'] } as never) as any
    expect(out.encoding.y.axis).toBeUndefined()
    expect(out.encoding.x.axis).toBeUndefined()
    expect(out.encoding.tooltip[0].format).toBeUndefined()
  })

  it('does not change the spec it is given', () => {
    const s = spec()
    applyChartFormats(s, { y: '.0%' })
    expect((s.encoding.y as Record<string, unknown>).axis).toBeUndefined()
  })

  it('accepts every string the .NET mapping writes', () => {
    for (const f of ['$,.0f', ',.2f', ',.2~f', '.0f', '.3f', '.0%', '.1%', '.2~s', '.4~s', '$.2f', '.10f', '.11~s']) expect(D3_FORMAT.test(f), f).toBe(true)
    for (const f of ['', 'f', '.f', '$$,.2f', ',.2g', '.100f', '%Y', '.2f;', 'datum.a', ".2f'"]) expect(D3_FORMAT.test(f), f).toBe(false)
  })
})
