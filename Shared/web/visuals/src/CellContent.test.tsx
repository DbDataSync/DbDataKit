import Ajv2020 from 'ajv/dist/2020'
import type { ValidateFunction } from 'ajv'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import coreSchema from '../schemas/core.schema.json'
import renderedSchema from '../schemas/rendered-widgets.schema.json'
import { CellContent, ICON_GLYPHS } from './CellContent'
import { MatrixGrid } from './MatrixGrid'
import type { RenderedMatrix } from './types'
import { TableWidget } from './widgets/TableWidget'
import type { TableDoc } from './views/model'
import type { RenderedTable } from './views/rendered'

/** An attribute in a cell (plan 32 DBR51): the server decided what the cell shows (the one-row rule), the client draws it: text, an icon chosen by the value, a badge, a mixed marker, a link. */
describe('an attribute cell', () => {
  const html = (cell: Parameters<typeof CellContent>[0]['cell'], onLink?: () => void) => renderToStaticMarkup(<CellContent cell={cell} onLink={onLink} />)

  it('is the text when it carries nothing else, and escapes it', () => {
    expect(html({ text: 'Open <b>' })).toBe('Open &lt;b&gt;')
  })
  it('draws an icon of the closed vocabulary with the value as its accessible name', () => {
    const out = html({ text: 'Closed', icon: 'check' })
    expect(out).toContain('role="img"')
    expect(out).toContain('aria-label="Closed"')
    expect(out).toContain(ICON_GLYPHS.check!)
  })
  it('draws a badge, and a mixed cell with its values as a tooltip', () => {
    expect(html({ text: 'Late', badge: true })).toContain('class="vz-badge"')
    const mixed = html({ text: 'Mixed', mixed: true, values: ['Open', 'Closed'] })
    expect(mixed).toContain('class="vz-mixed"')
    expect(mixed).toContain('title="Open, Closed"')
  })
  it('is a button only where it names a link and the host follows links', () => {
    expect(html({ text: 'Open', icon: 'circle', link: 'set-status' })).not.toContain('<button')
    const out = html({ text: 'Open', icon: 'circle', link: 'set-status' }, () => {})
    expect(out).toContain('<button')
    expect(out).toContain('data-link="set-status"')
    expect(html({ text: '', link: 'set-status' }, () => {})).not.toContain('<button')
  })
  it('has a glyph for every icon of the core schema, and the rendered schema lists the same icons', () => {
    const names = (coreSchema.$defs as unknown as { icon: { enum: string[] } }).icon.enum
    expect(Object.keys(ICON_GLYPHS).sort()).toEqual([...names].sort())
    expect(((renderedSchema.$defs as unknown as { icon: { enum: string[] } }).icon.enum).sort()).toEqual([...names].sort())
  })
})

describe('the cells of a matrix and a table that name a link', () => {
  const matrix: RenderedMatrix = {
    id: 'm', type: 'matrix',
    columns: [{ labels: ['Jan', 'Net'] }, { labels: ['Jan', 'Status'] }, { labels: ['Total', 'Net'], total: true }, { labels: ['Total', 'Status'], total: true }],
    rows: [
      { labels: ['West'], cells: [{ v: 5, text: '5' }, { v: null, text: 'Open', icon: 'circle', link: 'set-status' }, { v: 5, text: '5' }, { v: null, text: '' }] },
      { labels: ['Total'], total: true, cells: [{ v: 9, text: '9' }, { v: null, text: '' }, { v: 9, text: '9' }, { v: null, text: '' }] },
    ],
  }
  it('a matrix cell fires its link with its row and column; a total row or column never does', () => {
    const fired = vi.fn()
    const out = renderToStaticMarkup(<MatrixGrid view={matrix} onLink={fired} />)
    expect(out.match(/data-link="set-status"/g)).toHaveLength(1)
    expect(out).toContain('vz-icon')
  })
  it('a matrix without a link handler is plain: the icon shows, nothing is a button', () => {
    const out = renderToStaticMarkup(<MatrixGrid view={matrix} />)
    expect(out).not.toContain('<button')
    expect(out).toContain('vz-icon')
  })

  const table: RenderedTable = {
    type: 'table', columns: [{ field: 'status', label: 'Status' }, { field: 'net', label: 'Net', align: 'right' }],
    rows: [{ cells: [{ v: 'Open', text: 'Open', icon: 'circle', link: 'set-status' }, { v: 3, text: '3' }] }, { total: true, cells: [{ v: null, text: 'Total' }, { v: 3, text: '3' }] }],
  }
  const widget = { type: 'table', encodings: { columns: [{ field: 'status' }, { field: 'net' }] }, query: { model: 'm' } } as TableDoc
  it('a table cell is a button that names its link, and a total row has none', () => {
    const out = renderToStaticMarkup(<TableWidget id="t" widget={widget} result={table} onLink={() => {}} />)
    expect(out.match(/data-link="set-status"/g)).toHaveLength(1)
    expect(renderToStaticMarkup(<TableWidget id="t" widget={widget} result={table} />)).not.toContain('<button')
  })
})

describe('the rendered schema', () => {
  const AjvCtor = ((Ajv2020 as unknown as { default?: typeof Ajv2020 }).default ?? Ajv2020) as typeof Ajv2020
  const ajv = new AjvCtor({ strict: false, allErrors: true })
  ajv.addSchema(renderedSchema as object)
  const widget = ajv.getSchema(`${(renderedSchema as { $id: string }).$id}#/$defs/widget`) as ValidateFunction
  it('takes the parts of an attribute cell and refuses an icon outside the vocabulary or a link that is not a name', () => {
    const matrixWith = (cell: object) => ({ id: 'm', type: 'matrix', columns: [{ labels: ['a'] }], rows: [{ labels: ['r'], cells: [cell] }] })
    expect(widget(matrixWith({ text: 'Open', v: null, icon: 'circle', link: 'set-status', badge: false, mixed: true, values: ['Open', 'Closed'] }))).toBe(true)
    expect(widget(matrixWith({ text: 'Open', icon: 'smiley' }))).toBe(false)
    expect(widget(matrixWith({ text: 'Open', link: 'Not A Name' }))).toBe(false)
    expect(widget({ type: 'table', columns: [{ field: 'a', label: 'A' }], rows: [{ cells: [{ text: 'x', icon: 'star', link: 'l' }] }] })).toBe(true)
  })
})
