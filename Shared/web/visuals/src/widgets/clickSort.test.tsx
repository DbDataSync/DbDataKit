import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { RenderedMatrix } from '../types'
import type { TableDoc } from '../views/model'
import type { RenderedTable } from '../views/rendered'
import { ariaSort, nextSort, sortedOrder } from './clickSort'
import { MatrixWidget } from './MatrixWidget'
import { TableWidget } from './TableWidget'

const rows = [
  { cells: [{ v: 'b', text: 'b' }, { v: 10, text: '10' }] },
  { cells: [{ v: 'a', text: 'a' }, { v: 2, text: '2' }] },
  { cells: [{ v: 'c', text: 'c' }, { v: null, text: '' }] },
  { cells: [{ v: 'd', text: 'd' }, { v: 2, text: '2' }] },
  { total: true, cells: [{ v: null, text: 'Total' }, { v: 14, text: '14' }] },
]

describe('click-to-sort', () => {
  it('a click sorts ascending, a second reverses, a third gives the document order back; another column starts again', () => {
    let s = nextSort(null, 1)
    expect(s).toEqual({ index: 1, dir: 'asc' })
    s = nextSort(s, 1)
    expect(s).toEqual({ index: 1, dir: 'desc' })
    expect(nextSort(s, 1)).toBeNull()
    expect(nextSort(s, 0)).toEqual({ index: 0, dir: 'asc' })
  })

  it('orders numbers by value, text by code point, keeps equal rows in their order, an empty cell last and a total row after the rows', () => {
    expect(sortedOrder(rows, null)).toEqual([0, 1, 2, 3, 4])
    expect(sortedOrder(rows, { index: 1, dir: 'asc' })).toEqual([1, 3, 0, 2, 4])
    expect(sortedOrder(rows, { index: 1, dir: 'desc' })).toEqual([0, 1, 3, 2, 4])
    expect(sortedOrder(rows, { index: 0, dir: 'desc' })).toEqual([3, 2, 0, 1, 4])
  })

  it('says which header is sorted to a screen reader', () => {
    expect(ariaSort({ index: 1, dir: 'desc' }, 1)).toBe('descending')
    expect(ariaSort({ index: 1, dir: 'asc' }, 1)).toBe('ascending')
    expect(ariaSort({ index: 1, dir: 'asc' }, 0)).toBe('none')
    expect(ariaSort(null, 0)).toBe('none')
  })
})

describe('the widgets', () => {
  const widget: TableDoc = { type: 'table', title: 'T', encodings: { columns: [{ field: 'a' }, { field: 'n' }] }, query: { model: 'm' } }
  const table: RenderedTable = { type: 'table', columns: [{ field: 'a', label: 'A' }, { field: 'n', label: 'N' }], rows: rows as RenderedTable['rows'], total_rows: 9 }
  const matrix: RenderedMatrix = {
    id: 'mx', type: 'matrix', columns: [{ labels: ['X'] }, { labels: ['Y'] }],
    rows: [{ labels: ['r1'], cells: [{ v: 1, text: '1' }, { v: 3, text: '3' }] }, { labels: ['r2'], cells: [{ v: 2, text: '2' }, { v: 1, text: '1' }] }],
  }

  it('a table draws its headers as plain text unless viewers may sort, and then as buttons with aria-sort', () => {
    expect(renderToStaticMarkup(<TableWidget id="t" widget={widget} result={table} />)).not.toContain('aria-sort')
    const html = renderToStaticMarkup(<TableWidget id="t" widget={widget} result={table} sortable />)
    expect(html).toContain('aria-sort="none"')
    expect(html).toContain('data-testid="vz-sort-t-0"')
  })

  it('a table the server cut says what a sort over the loaded rows is only when asked; no sort, the plain notice', () => {
    const html = renderToStaticMarkup(<TableWidget id="t" widget={widget} result={table} sortable />)
    expect(html).toContain('4 of 9 rows')
    expect(html).not.toContain('Sorted over')
  })

  it('a matrix draws the innermost headers as buttons only when viewers may sort', () => {
    expect(renderToStaticMarkup(<MatrixWidget view={matrix} />)).not.toContain('vz-sort')
    const html = renderToStaticMarkup(<MatrixWidget view={matrix} sortable />)
    expect(html).toContain('data-testid="vz-sort-mx-0"')
    expect(html).toContain('aria-sort="none"')
  })
})
