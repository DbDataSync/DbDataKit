import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { parse } from 'yaml'
import { pageGeometry } from '../report/page'
import { layoutFlow } from '../report/layoutFlow'
import type { FlowLayoutDoc, ViewsDoc, WidgetDoc } from '../views/model'
import { canonicalText } from './canonical'
import { EditError } from './commands'
import type { EditCommand } from './commands'
import { edit, editAll, errorsOf, newDraft, undo } from './draft'
import type { Draft } from './draft'
import { diagnosticsFor, formTarget } from './diagnostics'
import { newDocumentText } from './newDocument'
import { runningTokens, unknownTokens } from './flow'

const here = path.dirname(fileURLToPath(import.meta.url))
const golden = fs.readFileSync(path.join(here, 'report-built.golden.yaml'), 'utf8').replace(/\r\n/g, '\n')

const start = () => newDraft('report', 'built', newDocumentText('report', 'Regional sales'))
const run = (draft: Draft, ...commands: EditCommand[]): Draft => {
  let next = draft
  for (const command of commands) {
    const r = edit(next, command)
    expect(r.error?.message).toBeUndefined()
    next = r.draft
  }
  return next
}
const refused = (draft: Draft, command: EditCommand): EditError => {
  const r = edit(draft, command)
  expect(r.error).toBeInstanceOf(EditError)
  expect(r.draft).toBe(draft)
  return r.error as EditError
}
const paged = (draft: Draft) => (draft.value as ViewsDoc).layouts[0] as FlowLayoutDoc
const sectionsOf = (draft: Draft) => paged(draft).sections as Array<{ title?: string; break?: string; blocks: Array<Record<string, unknown>> }>

const table: WidgetDoc = { type: 'table', title: 'Orders', encodings: { columns: [{ field: 'customers.region', label: 'Region' }, { field: 'sales.net_amount', label: 'Net revenue', align: 'right' }] }, query: { model: 'retail_sales' } }
const metric = (label: string): WidgetDoc => ({ type: 'metric', label, encodings: { value: 'sales.net_amount' }, query: { model: 'retail_sales' } })

/** A report as the editor builds it: widgets, a row of spans, a kept and a split block, a break, page setup, running text with tokens, a cover, strict and limits. */
function build(): Draft {
  let d = start()
  d = run(d,
    { type: 'addParameter', parameter: { name: 'quarter', type: 'string', default: 'FY27-Q1' } },
    { type: 'addWidget', id: 'intro', widget: { type: 'text', text: 'Net revenue for **{{params.quarter}}**.' } },
    { type: 'addWidget', id: 'net', widget: metric('Net revenue') },
    { type: 'addWidget', id: 'units', widget: metric('Units') },
    { type: 'addWidget', id: 'lines', widget: table },
    { type: 'addBlock', layout: 'paged', section: 0, block: { widget: 'intro' } },
    { type: 'addBlock', layout: 'paged', section: 0, block: { row: [{ widget: 'net', span: 6 }, { widget: 'units', span: 6 }] } },
    { type: 'addSection', layout: 'paged', title: 'Detail', widget: 'lines' },
    { type: 'setBlock', layout: 'paged', section: 1, block: 0, key: 'repeat_header', value: true },
    { type: 'addBlock', layout: 'paged', section: 1, block: { pagebreak: true }, index: 0 },
    { type: 'setSection', layout: 'paged', section: 1, key: 'break', value: 'before' },
    { type: 'setPage', layout: 'paged', field: 'size', value: 'A4' },
    { type: 'setPage', layout: 'paged', field: 'orientation', value: 'landscape' },
    { type: 'setMargin', layout: 'paged', side: 'left', value: 20 },
    { type: 'setRunningText', layout: 'paged', part: 'header', side: 'right', text: '{params.quarter}' },
    { type: 'setRunningText', layout: 'paged', part: 'header', side: 'left', text: '{title}' },
    { type: 'setRunningText', layout: 'paged', part: 'footer', side: 'center', text: 'Page {page} of {pages}' },
    { type: 'setCover', layout: 'paged', parameters: true },
    { type: 'setReportOption', key: 'cover.parameters', value: true },
    { type: 'setReportOption', key: 'limits.pages', value: 50 },
    { type: 'setReportOption', key: 'strict', value: true },
  )
  return d
}

describe('a report built in the editor', () => {
  it('equals the golden file, is valid, and is already in canonical order', () => {
    const d = build()
    expect(errorsOf(d)).toEqual([])
    expect(d.text).toBe(golden)
    expect(canonicalText(parse(d.text))).toBe(d.text)
  })

  it('is one undo step per command', () => {
    const d = build()
    expect(undo(d).text).not.toBe(d.text)
    expect(parse(undo(d).text).strict).toBeUndefined()
  })
})

describe('the page and the preview it drives', () => {
  it('setting the size, orientation and a margin changes the geometry the guides are drawn from; clearing them goes back to A4 with the default margins', () => {
    let d = start()
    const a4 = pageGeometry(paged(d).page)
    d = run(d, { type: 'setPage', layout: 'paged', field: 'size', value: 'A5' })
    const a5 = pageGeometry(paged(d).page)
    expect(a5.size).toBe('A5')
    expect(a5.contentHeightPx).toBeLessThan(a4.contentHeightPx)
    d = run(d, { type: 'setPage', layout: 'paged', field: 'orientation', value: 'landscape' }, { type: 'setMargin', layout: 'paged', side: 'top', value: 5 })
    const land = pageGeometry(paged(d).page)
    expect(land.widthMm).toBeGreaterThan(land.heightMm)
    expect(land.margins.top).toBe(5)
    expect(layoutFlow(paged(d), {}).page.cssSize).toBe('A5 landscape')
    d = run(d, { type: 'setPage', layout: 'paged', field: 'size', value: { w: 100, h: 200 } })
    // the orientation set above still applies to a custom size: turned to landscape
    expect(pageGeometry(paged(d).page).cssSize).toBe('200mm 100mm')
  })

  it('removes what is emptied: no `page: {}` and no `margins: {}` is left behind', () => {
    let d = run(start(), ...(['top', 'right', 'bottom'] as const).map((side): EditCommand => ({ type: 'setMargin', layout: 'paged', side, value: undefined })), { type: 'setMargin', layout: 'paged', side: 'left', value: 12 })
    expect(parse(d.text).layouts[0].page.margins).toEqual({ left: 12 })
    d = run(d, { type: 'setMargin', layout: 'paged', side: 'left', value: undefined })
    expect(parse(d.text).layouts[0].page.margins).toBeUndefined()
    d = run(d, { type: 'setPage', layout: 'paged', field: 'size', value: undefined }, { type: 'setPage', layout: 'paged', field: 'orientation', value: undefined })
    expect(parse(d.text).layouts[0].page).toBeUndefined()
    // clearing what is not there is no change and no error
    expect(run(d, { type: 'setPage', layout: 'paged', field: 'size', value: undefined }).text).toBe(d.text)
  })

  it('refuses what is not a size, an orientation, a margin or a layout of the right type', () => {
    const d = start()
    expect(refused(d, { type: 'setPage', layout: 'paged', field: 'orientation', value: 'sideways' }).code).toBe('bad_value')
    expect(refused(d, { type: 'setPage', layout: 'paged', field: 'size', value: { w: 10, h: Number.NaN } }).code).toBe('bad_value')
    expect(refused(d, { type: 'setMargin', layout: 'paged', side: 'top', value: Number.NaN }).code).toBe('bad_value')
    expect(refused(d, { type: 'setMargin', layout: 'nope', side: 'top', value: 1 }).code).toBe('unknown_layout')
    const withCanvas = run(d, { type: 'addLayout', layout: { name: 'wall', type: 'canvas', canvas: { width: 1920, height: 1080 }, tiles: [] } })
    expect(refused(withCanvas, { type: 'setPage', layout: 'wall', field: 'size', value: 'A4' }).code).toBe('not_flow')
  })

  it('a page smaller than the validator allows is the validator\'s to say, beside the page', () => {
    const d = run(start(), { type: 'setPage', layout: 'paged', field: 'size', value: { w: 10, h: 10 } })
    const own = errorsOf(d).filter((e) => e.path.startsWith('layouts[0].page'))
    expect(own[0]?.code).toBe('DBD-VIEW-030')
    expect(formTarget(own[0]!.path)).toEqual({ area: 'layout', index: 0, field: 'page.size' })
  })
})

describe('header and footer text', () => {
  it('an unknown {token} is refused at its field, by the validator, and the tokens of the vocabulary and of a declared parameter are not', () => {
    let d = run(start(), { type: 'addParameter', parameter: { name: 'quarter', type: 'string' } })
    for (const ok of ['{title}', '{page}', '{pages}', '{asof}', '{viewer}', '{params.quarter}', 'Page {page} of {pages}']) {
      d = run(d, { type: 'setRunningText', layout: 'paged', part: 'footer', side: 'left', text: ok })
      expect(errorsOf(d).filter((e) => e.path.includes('footer'))).toEqual([])
    }
    d = run(d, { type: 'setRunningText', layout: 'paged', part: 'header', side: 'center', text: 'Run {nope}' })
    const bad = errorsOf(d).filter((e) => e.path.startsWith('layouts[0].header'))
    expect(bad).toHaveLength(1)
    expect(bad[0]).toMatchObject({ code: 'DBD-VIEW-030', path: 'layouts[0].header.center' })
    expect(bad[0]!.line).toBeGreaterThan(1)
    expect(diagnosticsFor(d.validation.diagnostics, { area: 'layout', index: 0 }).map((x) => x.path)).toContain('layouts[0].header.center')
    // an undeclared parameter is another code, at the same kind of place
    d = run(d, { type: 'setRunningText', layout: 'paged', part: 'header', side: 'center', text: '{params.region}' })
    expect(errorsOf(d).filter((e) => e.path.startsWith('layouts[0].header'))[0]).toMatchObject({ code: 'DBD-VIEW-008', path: 'layouts[0].header.center' })
  })

  it('empty text removes the side, and the part when it is the last', () => {
    let d = run(start(), { type: 'setRunningText', layout: 'paged', part: 'header', side: 'left', text: '{title}' }, { type: 'setRunningText', layout: 'paged', part: 'header', side: 'right', text: '{asof}' })
    d = run(d, { type: 'setRunningText', layout: 'paged', part: 'header', side: 'left', text: '' })
    expect(parse(d.text).layouts[0].header).toEqual({ right: '{asof}' })
    d = run(d, { type: 'setRunningText', layout: 'paged', part: 'header', side: 'right', text: undefined })
    expect(parse(d.text).layouts[0].header).toBeUndefined()
  })

  it('lists the tokens a form offers, and finds the unknown ones while typing', () => {
    const doc = { parameters: [{ name: 'quarter', type: 'string' as const }] }
    expect(runningTokens(doc)).toEqual(['{title}', '{page}', '{pages}', '{asof}', '{viewer}', '{params.quarter}'])
    expect(unknownTokens('{title} {oops} {params.quarter} {params.x} {}', doc)).toEqual(['oops', 'params.x', ''])
    expect(unknownTokens('no tokens, a { alone', doc)).toEqual([])
  })
})

describe('sections, blocks and rows', () => {
  const inRows = (d: Draft) => errorsOf(d).filter((e) => e.path.includes('row'))
  const withBlocks = () => run(start(),
    { type: 'addWidget', id: 'a', widget: metric('A') }, { type: 'addWidget', id: 'b', widget: metric('B') }, { type: 'addWidget', id: 'c', widget: table },
    { type: 'addBlock', layout: 'paged', section: 0, block: { widget: 'a' } },
    { type: 'addBlock', layout: 'paged', section: 0, block: { widget: 'c' } },
    { type: 'addSection', layout: 'paged', title: 'Two', widget: 'b' })

  it('adds, moves and removes sections; the last one stays', () => {
    let d = withBlocks()
    expect(sectionsOf(d).map((s) => s.title)).toEqual(['Summary', 'Two'])
    d = run(d, { type: 'moveSection', layout: 'paged', section: 1, to: 0 })
    expect(sectionsOf(d).map((s) => s.title)).toEqual(['Two', 'Summary'])
    d = run(d, { type: 'addSection', layout: 'paged', index: 1 })
    expect(sectionsOf(d)).toHaveLength(3)
    expect(sectionsOf(d)[1]).toEqual({ blocks: [] })
    expect(errorsOf(d).some((e) => e.path === 'layouts[0].sections[1].blocks')).toBe(true)
    d = run(d, { type: 'removeSection', layout: 'paged', section: 1 }, { type: 'removeSection', layout: 'paged', section: 1 })
    expect(refused(d, { type: 'removeSection', layout: 'paged', section: 0 }).code).toBe('last_section')
    expect(refused(d, { type: 'removeSection', layout: 'paged', section: 4 }).code).toBe('bad_index')
  })

  it('sets and clears a section title and its break; a break is `before` or `after`', () => {
    let d = withBlocks()
    d = run(d, { type: 'setSection', layout: 'paged', section: 0, key: 'break', value: 'after' }, { type: 'setSection', layout: 'paged', section: 0, key: 'title', value: 'Renamed' })
    expect(sectionsOf(d)[0]).toMatchObject({ title: 'Renamed', break: 'after' })
    expect(Object.keys(sectionsOf(d)[0]!)).toEqual(['title', 'break', 'blocks'])
    d = run(d, { type: 'setSection', layout: 'paged', section: 0, key: 'title', value: '' }, { type: 'setSection', layout: 'paged', section: 0, key: 'break', value: undefined })
    expect(Object.keys(sectionsOf(d)[0]!)).toEqual(['blocks'])
    expect(refused(d, { type: 'setSection', layout: 'paged', section: 0, key: 'break', value: 'sometimes' }).code).toBe('bad_value')
  })

  it('moves a block within its section and removes it', () => {
    let d = withBlocks()
    d = run(d, { type: 'moveBlock', layout: 'paged', section: 0, block: 1, to: 0 })
    expect(sectionsOf(d)[0]!.blocks.map((b) => b.widget)).toEqual(['c', 'a'])
    d = run(d, { type: 'moveBlock', layout: 'paged', section: 0, block: 0, to: 9 })
    expect(sectionsOf(d)[0]!.blocks.map((b) => b.widget)).toEqual(['a', 'c'])
    d = run(d, { type: 'removeBlock', layout: 'paged', section: 0, block: 0 })
    expect(sectionsOf(d)[0]!.blocks.map((b) => b.widget)).toEqual(['c'])
    expect(refused(d, { type: 'removeBlock', layout: 'paged', section: 0, block: 3 }).code).toBe('bad_index')
  })

  it('keep, repeat_header and height are set on a widget block and removed with it; a row has keep only; a page break has nothing', () => {
    let d = withBlocks()
    d = run(d, { type: 'setBlock', layout: 'paged', section: 0, block: 1, key: 'keep', value: true }, { type: 'setBlock', layout: 'paged', section: 0, block: 1, key: 'repeat_header', value: false }, { type: 'setBlock', layout: 'paged', section: 0, block: 0, key: 'height', value: 60 })
    expect(sectionsOf(d)[0]!.blocks[1]).toEqual({ widget: 'c', keep: true, repeat_header: false })
    expect(sectionsOf(d)[0]!.blocks[0]).toEqual({ widget: 'a', height: 60 })
    expect(layoutFlow(paged(d), d.value!.widgets).sections[0]!.blocks[1]).toMatchObject({ keep: true, split: false })
    d = run(d, { type: 'setBlock', layout: 'paged', section: 0, block: 1, key: 'keep', value: undefined })
    expect(sectionsOf(d)[0]!.blocks[1]).toEqual({ widget: 'c', repeat_header: false })
    d = run(d, { type: 'addBlock', layout: 'paged', section: 0, block: { row: [{ widget: 'a', span: 12 }] } }, { type: 'addBlock', layout: 'paged', section: 0, block: { pagebreak: true } })
    expect(refused(d, { type: 'setBlock', layout: 'paged', section: 0, block: 2, key: 'height', value: 10 }).code).toBe('bad_value')
    expect(refused(d, { type: 'setBlock', layout: 'paged', section: 0, block: 3, key: 'keep', value: true }).code).toBe('bad_value')
    expect(refused(d, { type: 'setBlock', layout: 'paged', section: 0, block: 0, key: 'keep', value: 'yes' as unknown as boolean }).code).toBe('bad_value')
    expect(run(d, { type: 'setBlock', layout: 'paged', section: 0, block: 2, key: 'keep', value: false }).text).toContain('keep: false')
  })

  it('a block names a widget that exists; a row is cells with spans of 1 to 12 and at most 12 of them', () => {
    const d = withBlocks()
    expect(refused(d, { type: 'addBlock', layout: 'paged', section: 0, block: { widget: 'ghost' } }).code).toBe('unknown_widget')
    expect(refused(d, { type: 'addBlock', layout: 'paged', section: 0, block: { row: [] } }).code).toBe('bad_value')
    expect(refused(d, { type: 'addBlock', layout: 'paged', section: 0, block: { row: [{ widget: 'a', span: 0 }] } }).code).toBe('bad_value')
    expect(refused(d, { type: 'addBlock', layout: 'paged', section: 0, block: { row: [{ widget: 'a', span: 2.5 }] } }).code).toBe('bad_value')
  })

  it('the cells of a row: add, change, remove; spans that add up past twelve are the validator\'s to say, at the row', () => {
    // a layout places a widget once, so the row's widgets are ones nothing else places
    let d = run(start(), { type: 'addWidget', id: 'a', widget: metric('A') }, { type: 'addWidget', id: 'b', widget: metric('B') }, { type: 'addWidget', id: 'c', widget: table },
      { type: 'addBlock', layout: 'paged', section: 0, block: { row: [{ widget: 'a', span: 6 }] } })
    d = run(d, { type: 'addCell', layout: 'paged', section: 0, block: 0, cell: { widget: 'b', span: 6 } })
    expect(inRows(d)).toEqual([])
    d = run(d, { type: 'setCell', layout: 'paged', section: 0, block: 0, cell: 0, changes: { span: 8 } })
    const over = inRows(d)
    expect(over).toHaveLength(1)
    expect(over[0]).toMatchObject({ code: 'DBD-VIEW-030', path: 'layouts[0].sections[0].blocks[0].row' })
    d = run(d, { type: 'setCell', layout: 'paged', section: 0, block: 0, cell: 0, changes: { span: 6, widget: 'c' } })
    expect(inRows(d)).toEqual([])
    expect(sectionsOf(d)[0]!.blocks[0]).toEqual({ row: [{ widget: 'c', span: 6 }, { widget: 'b', span: 6 }] })
    d = run(d, { type: 'removeCell', layout: 'paged', section: 0, block: 0, cell: 1 })
    expect(refused(d, { type: 'removeCell', layout: 'paged', section: 0, block: 0, cell: 0 }).code).toBe('bad_value')
    expect(refused(d, { type: 'setCell', layout: 'paged', section: 0, block: 0, cell: 5, changes: { span: 3 } }).code).toBe('bad_index')
    d = run(d, { type: 'addBlock', layout: 'paged', section: 0, block: { widget: 'a' } })
    expect(refused(d, { type: 'addCell', layout: 'paged', section: 0, block: 1, cell: { widget: 'b', span: 3 } }).code).toBe('bad_value')
  })

  it('removing a widget takes its blocks and cells with it, and a row left with no cell goes whole; renaming follows every place', () => {
    let d = run(withBlocks(), { type: 'addBlock', layout: 'paged', section: 0, block: { row: [{ widget: 'a', span: 4 }, { widget: 'b', span: 4 }] } }, { type: 'addBlock', layout: 'paged', section: 0, block: { row: [{ widget: 'a', span: 6 }, { widget: 'a', span: 6 }] } })
    d = run(d, { type: 'renameWidget', id: 'b', to: 'beta' })
    expect(JSON.stringify(sectionsOf(d))).toContain('"widget":"beta"')
    expect(JSON.stringify(sectionsOf(d))).not.toContain('"widget":"b"')
    d = run(d, { type: 'removeWidget', id: 'a' })
    expect(sectionsOf(d)[0]!.blocks).toEqual([{ widget: 'c' }, { row: [{ widget: 'beta', span: 4 }] }])
    expect(errorsOf(d).filter((e) => e.path.includes('row'))).toEqual([])
  })

  it('commands that make several changes are one undo step (editAll), and a new key lands where the canonical order puts it', () => {
    const d = withBlocks()
    const r = editAll(d, [{ type: 'setBlock', layout: 'paged', section: 0, block: 0, key: 'height', value: 40 }, { type: 'setBlock', layout: 'paged', section: 0, block: 0, key: 'keep', value: true }])
    expect(r.error).toBeUndefined()
    expect(undo(r.draft).text).toBe(d.text)
    expect(Object.keys(sectionsOf(r.draft)[0]!.blocks[0]!)).toEqual(['widget', 'height', 'keep'])
  })
})

describe('the report options', () => {
  it('strict, limits and the cover default are set in canonical places and removed with their group', () => {
    let d = run(start(), { type: 'setReportOption', key: 'limits.rows', value: 5000 }, { type: 'setReportOption', key: 'limits.pages', value: 20 }, { type: 'setReportOption', key: 'strict', value: false })
    expect(Object.keys(parse(d.text))).toEqual(['schemaVersion', 'kind', 'title', 'strict', 'limits', 'widgets', 'layouts'])
    expect(parse(d.text).limits).toEqual({ rows: 5000, pages: 20 })
    d = run(d, { type: 'setReportOption', key: 'limits.rows', value: undefined })
    expect(parse(d.text).limits).toEqual({ pages: 20 })
    d = run(d, { type: 'setReportOption', key: 'limits.pages', value: undefined })
    expect(parse(d.text).limits).toBeUndefined()
    expect(errorsOf(d).filter((e) => /^(strict|limits)/.test(e.path))).toEqual([])
  })

  it('refuses a value of the wrong type, a dashboard, and shows a limit out of range as the validator\'s', () => {
    const d = start()
    expect(refused(d, { type: 'setReportOption', key: 'strict', value: 1 }).code).toBe('bad_value')
    expect(refused(d, { type: 'setReportOption', key: 'limits.pages', value: 2.5 }).code).toBe('bad_value')
    const dash = newDraft('dashboard', 'd', newDocumentText('dashboard', 'D'))
    expect(refused(dash, { type: 'setReportOption', key: 'strict', value: true }).code).toBe('not_flow')
    const high = run(d, { type: 'setReportOption', key: 'limits.pages', value: 9999 })
    expect(errorsOf(high).length).toBeGreaterThan(0)
  })
})
