/**
 * Click-to-sort for viewers (plan 32 DBR45): a header of a table or a matrix sorts the rows on screen. It is never stored in the document (the document's `sort` is the default, and what print and export use),
 * and it sorts what was loaded: a click sorts, a second click reverses, a third gives the document's order back. When the server cut the result at its limit, a host that can run the query again with
 * `order` does so (`onServerSort`) and the notice says the sort is the server's; without one the loaded rows are sorted and the notice says how many of how many.
 */

export type SortDirection = 'asc' | 'desc'
export interface ViewerSort { index: number; dir: SortDirection }

/** What a click on the header of column `index` does to the sort now: none, then ascending, then descending, then none again; another column starts ascending. */
export function nextSort(current: ViewerSort | null, index: number): ViewerSort | null {
  if (current === null || current.index !== index) return { index, dir: 'asc' }
  return current.dir === 'asc' ? { index, dir: 'desc' } : null
}

/** The `aria-sort` of the header of column `index`. */
export function ariaSort(current: ViewerSort | null, index: number): 'ascending' | 'descending' | 'none' {
  return current !== null && current.index === index ? (current.dir === 'asc' ? 'ascending' : 'descending') : 'none'
}

interface Sortable { total?: boolean; cells: Array<{ v?: unknown; text: string }> }

const key = (cell: { v?: unknown; text: string } | undefined): { number: number | null; text: string } | null => {
  if (cell === undefined) return null
  const v = cell.v
  if (typeof v === 'number') return { number: v, text: cell.text }
  if (typeof v === 'string' && v !== '') return { number: null, text: v }
  return cell.text === '' ? null : { number: null, text: cell.text }
}

/** Two cells under one column: numbers by value, other text by code point (the same on every machine), an empty cell after every other whichever the direction. */
function compare(a: ReturnType<typeof key>, b: ReturnType<typeof key>, dir: SortDirection): number {
  if (a === null || b === null) return a === b ? 0 : a === null ? 1 : -1
  const order = a.number !== null && b.number !== null ? a.number - b.number : a.text < b.text ? -1 : a.text > b.text ? 1 : 0
  return dir === 'asc' ? order : -order
}

/**
 * The order of a result's rows under a sort, as indexes into `rows`: the data rows sorted (equal rows keep their order), a total row after them in the order it had. With no sort the order is the document's.
 * A matrix's column and a table's column are the same thing here: the cell at `index` of each row.
 */
export function sortedOrder(rows: readonly Sortable[], sort: ViewerSort | null): number[] {
  const all = rows.map((_, i) => i)
  if (sort === null) return all
  const data = all.filter((i) => !rows[i]!.total)
  const totals = all.filter((i) => rows[i]!.total)
  data.sort((x, y) => compare(key(rows[x]!.cells[sort.index]), key(rows[y]!.cells[sort.index]), sort.dir) || x - y)
  return [...data, ...totals]
}
