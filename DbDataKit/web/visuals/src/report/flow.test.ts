import { describe, expect, it } from 'vitest'
import { validateText } from '../client/text'
import { sampleBundle } from '../testing/sampleBundle'
import type { FlowLayoutDoc, ViewsDoc, WidgetDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { BLOCK_GAP, FLOW_FONT_STACK, flowCss } from './css'
import { layoutFlow, plannedWidgets } from './layoutFlow'
import { PAGE_SIZES_MM, mmToPx, pageGeometry } from './page'
import { estimatedTops, paginate, planItems, startY, type PaginationItem } from './paginate'
import { describeParameter, expandTokens, formatAsOf, runningText } from './tokens'

const bundle = sampleBundle()
const report = validateText(bundle.documents['report/retail-sales-monthly']!).document as ViewsDoc
const results = bundle.results!['report/retail-sales-monthly'] as Record<string, RenderedWidget>
const flow = report.layouts.find((l) => l.type === 'flow') as FlowLayoutDoc

describe('the page', () => {
  it('A4 portrait with the plan\'s margins: 180 x 257 mm printable, in CSS pixels at 96 per inch', () => {
    const p = pageGeometry(flow.page)
    expect(p).toMatchObject({ size: 'A4', orientation: 'portrait', widthMm: 210, heightMm: 297, contentWidthMm: 180, contentHeightMm: 257, cssSize: 'A4 portrait' })
    expect(p.widthPx).toBe(793.7)
    expect(p.contentWidthPx).toBe(680.31)
    expect(p.contentHeightPx).toBe(971.34)
    expect(mmToPx(25.4)).toBe(96)
  })

  it('every named size, landscape and a custom size', () => {
    expect(Object.keys(PAGE_SIZES_MM)).toEqual(['A3', 'A4', 'A5', 'Letter', 'Legal'])
    expect(pageGeometry({ size: 'Letter', orientation: 'landscape' })).toMatchObject({ widthMm: 279.4, heightMm: 215.9, orientation: 'landscape', cssSize: 'Letter landscape' })
    expect(pageGeometry({ size: { w: 100, h: 200 }, margins: { top: 10, right: 10, bottom: 10, left: 10 } })).toMatchObject({ size: 'custom', contentWidthMm: 80, contentHeightMm: 180, cssSize: '100mm 200mm' })
    expect(pageGeometry({ size: { w: 100, h: 200 }, orientation: 'landscape' })).toMatchObject({ widthMm: 200, heightMm: 100 })
    expect(pageGeometry(undefined)).toMatchObject({ size: 'A4', margins: { top: 20, right: 15, bottom: 20, left: 15 } })
    expect(pageGeometry({ margins: { top: 5 } }).margins).toEqual({ top: 5, right: 15, bottom: 20, left: 15 })
  })
})

describe('the running text', () => {
  const context = { title: 'Retail sales by month', page: 2, pages: 5, asOf: '2026-11-08T09:30:00Z', viewer: 'Ada', parameters: { region: ['West', 'East'], none: [], on: true } }
  it('expands every token of the vocabulary', () => {
    expect(expandTokens('{title} - page {page} of {pages} - {asof} - {viewer} - {params.region} / {params.none} / {params.on}', context)).toBe('Retail sales by month - page 2 of 5 - 2026-11-08 09:30 UTC - Ada - West, East / all / yes')
  })
  it('leaves what is not a token alone and never reads text as markup', () => {
    expect(expandTokens('{nope} {params.missing} <b>{title}</b>', { ...context, title: '<i>x</i>' })).toBe('{nope} all <b><i>x</i></b>')
    expect(describeParameter(null)).toBe('all')
    expect(formatAsOf('yesterday')).toBe('yesterday')
  })
  it('a header is its three parts; one with nothing to say is none', () => {
    expect(runningText({ left: '{title}', right: '{asof}' }, context)).toEqual({ left: 'Retail sales by month', center: '', right: '2026-11-08 09:30 UTC' })
    expect(runningText({ center: 'Page {page} of {pages}' }, context)).toEqual({ left: '', center: 'Page 2 of 5', right: '' })
    expect(runningText({ left: '{viewer}' }, { ...context, viewer: null })).toBeNull()
    expect(runningText(undefined, context)).toBeNull()
  })
})

describe('layoutFlow', () => {
  const plan = layoutFlow(flow, report.widgets)
  it('reads the sample report: sections, a row of spans, a table that splits and repeats its head, a chart kept whole', () => {
    expect(plan.layout).toBe('paged')
    expect(plan.cover).toBe(false)
    expect(plan.header).toEqual({ left: '{title}', right: '{asof}' })
    expect(plan.sections.map((s) => s.title)).toEqual(['Summary', 'Trend', 'Products'])
    expect(plan.sections.flatMap((s) => s.blocks)).toEqual([
      { kind: 'widget', key: 's0b0', widget: 'intro', heightMm: undefined, keep: false, split: false, repeatHeader: false },
      { kind: 'row', key: 's0b1', cells: [{ widget: 'net', span: 4 }, { widget: 'margin', span: 4 }, { widget: 'units', span: 4 }], heightMm: undefined, keep: true },
      { kind: 'widget', key: 's1b0', widget: 'by_month', heightMm: 80, keep: true, split: false, repeatHeader: false },
      { kind: 'widget', key: 's2b0', widget: 'products', heightMm: undefined, keep: false, split: true, repeatHeader: true },
    ])
    expect(plannedWidgets(plan)).toEqual(['intro', 'net', 'margin', 'units', 'by_month', 'products'])
  })
  it('an explicit keep, repeat_header: false, breaks and a cover are carried', () => {
    const widgets: Record<string, Pick<WidgetDoc, 'type'>> = { t: { type: 'table' }, c: { type: 'chart' } }
    const p = layoutFlow({ name: 'x', type: 'flow', cover: { parameters: true }, sections: [{ title: 'A', break: 'before', blocks: [{ widget: 't', keep: true }, { pagebreak: true }, { widget: 'c', keep: false }] }, { break: 'after', blocks: [{ widget: 't', repeat_header: false }] }] } as FlowLayoutDoc, widgets)
    expect(p.cover).toBe(true)
    expect(p.sections.map((s) => [s.breakBefore, s.breakAfter])).toEqual([[true, false], [false, true]])
    expect(p.sections[0]!.blocks).toEqual([
      { kind: 'widget', key: 's0b0', widget: 't', heightMm: undefined, keep: true, split: false, repeatHeader: false },
      { kind: 'break', key: 's0b1' },
      { kind: 'widget', key: 's0b2', widget: 'c', heightMm: undefined, keep: false, split: false, repeatHeader: false },
    ])
    expect(p.sections[1]!.blocks[0]).toMatchObject({ split: true, repeatHeader: false })
  })
})

describe('the stylesheet', () => {
  it('has the page, the breaks, the repeated head and the table rules of section 4.1, in the theme font stack', async () => {
    const css = flowCss(pageGeometry(flow.page))
    expect(css).toContain('@page { size: A4 portrait; margin: 20mm 15mm 20mm 15mm; }')
    for (const rule of ['break-before: page', 'break-after: page', 'break-inside: avoid', 'break-after: avoid', 'orphans: 2', 'widows: 2', 'display: table-header-group', 'tfoot { display: table-row-group', '@media print']) expect(css).toContain(rule)
    expect(css).toContain(FLOW_FONT_STACK)
    expect(FLOW_FONT_STACK).toMatch(/^system-ui.*'DbDataKit Sans', sans-serif$/)
    expect(css.indexOf("'DbDataKit Sans'")).toBeGreaterThan(css.indexOf('system-ui'))
    await expect(`${css}\n`).toMatchFileSnapshot('./__snapshots__/flow-a4.css')
    expect(flowCss(pageGeometry({ size: { w: 100, h: 200 } }))).toContain('size: 100mm 200mm')
  })
})

/** An item of a given height (the gap is in it). */
const item = (key: string, height: number, over: Partial<PaginationItem> = {}): PaginationItem => ({ key, height, ...over })
const table = (key: string, rows: number, over: Partial<PaginationItem> = {}): PaginationItem => ({ key, height: 40 + 30 + rows * 10 + BLOCK_GAP, table: { headerHeight: 30, rowHeights: Array(rows).fill(10), repeatHeader: true }, ...over })

describe('paginate', () => {
  it('blocks that fit share a page; one that does not moves whole to the next', () => {
    const r = paginate([item('a', 60), item('b', 30), item('c', 50, { keep: true })], 100)
    expect(r.pages.map((p) => p.map((x) => x.key))).toEqual([['a', 'b'], ['c']])
    expect(r.starts).toEqual([{ page: 2, key: 'c' }])
  })
  it('a forced break, a section break before and after, and no empty page at the top', () => {
    const r = paginate([item('a', 10, { breakBefore: true }), item('b', 10, { breakAfter: true }), item('c', 10), item('d', 10, { breakBefore: true })], 1000)
    expect(r.pages.map((p) => p.map((x) => x.key))).toEqual([['a', 'b'], ['c'], ['d']])
    expect(paginate([item('p', 0, { breakAfter: true }), item('q', 10)], 100).pages.map((p) => p.length)).toEqual([1, 1])
  })
  it('a title stays with the block after it', () => {
    const r = paginate([item('a', 80), item('t', 20, { keep: true, keepWithNext: true }), item('b', 30, { keep: true })], 110)
    expect(r.pages.map((p) => p.map((x) => x.key))).toEqual([['a'], ['t', 'b']])
  })
  it('a table splits between rows and repeats its head on every page; a row is never split', () => {
    const t = table('t', 25) // the tile's 52 above the head, a head of 30, rows of 10
    const r = paginate([t], 150)
    expect(r.pages.map((p) => p.map((x) => `${x.rowFrom}-${x.rowTo}${x.continued ? ' cont' : ''}`))).toEqual([['0-6'], ['6-18 cont'], ['18-25 cont']])
    expect(r.starts).toEqual([{ page: 2, key: 't', row: 6 }, { page: 3, key: 't', row: 18 }])
    // without a repeated head the continuation pages hold 3 more rows (15, not 12)
    const noHead = paginate([{ ...t, table: { ...t.table!, repeatHeader: false } }], 150)
    expect(noHead.pages.map((p) => p[0]!.rowTo! - p[0]!.rowFrom!)).toEqual([6, 15, 4])
  })
  it('a table that starts too low on a page to show a row starts on the next', () => {
    const r = paginate([item('a', 100), table('t', 3)], 160)
    expect(r.pages.map((p) => p.map((x) => x.key))).toEqual([['a'], ['t']])
    expect(r.starts).toEqual([{ page: 2, key: 't', row: 0 }])
  })
  it('a block taller than a page that is not kept runs over; one that is kept starts at the top and overflows', () => {
    const r = paginate([item('a', 40), item('big', 230)], 100)
    expect(r.starts).toEqual([{ page: 2, key: 'big', offset: 60 }, { page: 3, key: 'big', offset: 160 }])
    expect(paginate([item('a', 40), item('big', 230, { keep: true })], 100).starts).toEqual([{ page: 2, key: 'big' }])
  })
})

describe('the sample report on a page', () => {
  const plan = layoutFlow(flow, report.widgets)
  const items = planItems(plan, report.widgets, results)
  it('is estimated from its results: the summary, the chart and the first rows of the table of 61 (the total last) fill page one, the table runs on over two more', () => {
    expect(items.map((i) => [i.key, Math.round(i.height), i.keep ?? false, i.table ? i.table.rowHeights.length : 0])).toEqual([
      ['s0t', 40, true, 0],
      ['s0b0', 142, false, 0],
      ['s0b1', 118, true, 0],
      ['s1t', 40, true, 0],
      ['s1b0', 314, true, 0],
      ['s2t', 40, true, 0],
      ['s2b0', 1796, false, 61],
    ])
    const pagination = paginate(items, plan.page.contentHeightPx)
    const label = (x: { key: string; rowFrom?: number; rowTo?: number }) => (x.rowFrom === undefined ? x.key : `${x.key}[${x.rowFrom}-${x.rowTo}]`)
    expect(pagination.pages.map((p) => p.map(label))).toEqual([['s0t', 's0b0', 's0b1', 's1t', 's1b0', 's2t', 's2b0[0-6]'], ['s2b0[6-39]'], ['s2b0[39-61]']])
    expect(pagination.starts).toEqual([{ page: 2, key: 's2b0', row: 6 }, { page: 3, key: 's2b0', row: 39 }])
  })
  it('puts the guides where the pages start: measured where the browser measured, estimated where it did not', () => {
    const pagination = paginate(items, plan.page.contentHeightPx)
    const tops = estimatedTops(items)
    expect(tops.s0t).toBe(0)
    expect(startY(pagination.starts[0]!, items)).toBeGreaterThan(tops.s2b0!)
    const measured = { s2b0: { top: 700, height: 1800, header: 30, rows: Array.from({ length: 61 }, (_, i) => ({ top: 760 + i * 28, height: 28 })) } }
    expect(startY({ page: 2, key: 's2b0', row: 3 }, items, measured)).toBe(760 + 3 * 28)
    expect(startY({ page: 2, key: 's2b0', offset: 100 }, items, measured)).toBe(800)
  })
})
