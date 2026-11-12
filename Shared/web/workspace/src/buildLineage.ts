/**
 * The column lineage of a Build draft as `dbdatabuild project show graph --columns` answers it (plan 33 SC20h), read from the document of the real tool. The tool's `data` has `nodes` and `edges` (the models), `column_edges` (which column of which
 * table every output column of a built model comes from, and by what transform) and, when a column was asked for, `column_lineage` (what it comes from and what is built from it, with the number of steps). The reader accepts only what it can place and says
 * what it dropped, so a change in the tool's shape shows up as a sentence on the screen and not as a wrong picture.
 */
export interface ColumnEdge { fromTable: string | null; fromColumn: string | null; toModel: string; toColumn: string; transform: string }
export interface ColumnStep { table: string; column: string; distance: number }
export interface ColumnLineage { column: string; upstream: ColumnStep[]; downstream: ColumnStep[] }
export interface LineageRead {
  columnEdges: ColumnEdge[]
  lineage: ColumnLineage | null
  /** Entries the reader could not place (a shape it does not know), counted, never guessed at. */
  dropped: number
}

const obj = (v: unknown): Record<string, unknown> | null => (typeof v === 'object' && v !== null && !Array.isArray(v) ? (v as Record<string, unknown>) : null)
const str = (v: unknown): string | null => (typeof v === 'string' && v !== '' ? v : null)

export function readLineage(data: unknown): LineageRead {
  const root = obj(data)
  let dropped = 0
  const columnEdges: ColumnEdge[] = []
  for (const raw of Array.isArray(root?.column_edges) ? root.column_edges : []) {
    const e = obj(raw)
    const toModel = str(e?.to_model)
    const toColumn = str(e?.to_column)
    if (!e || !toModel || !toColumn) { dropped++; continue }
    columnEdges.push({ fromTable: str(e.from_table), fromColumn: str(e.from_column), toModel, toColumn, transform: str(e.transform) ?? '' })
  }
  const steps = (list: unknown): ColumnStep[] => {
    const out: ColumnStep[] = []
    for (const raw of Array.isArray(list) ? list : []) {
      const s = obj(raw)
      const table = str(s?.table)
      const column = str(s?.column)
      if (!s || !table || !column) { dropped++; continue }
      out.push({ table, column, distance: typeof s.distance === 'number' ? s.distance : 1 })
    }
    return out
  }
  const l = obj(root?.column_lineage)
  const lineage = l && str(l.column) ? { column: str(l.column)!, upstream: steps(l.upstream), downstream: steps(l.downstream) } : null
  return { columnEdges, lineage, dropped }
}

/** The columns a person can follow: every output column of a built model (`model.column`), sorted. */
export function followableColumns(edges: readonly ColumnEdge[]): string[] {
  return [...new Set(edges.map((e) => `${e.toModel}.${e.toColumn}`))].sort((a, b) => a.localeCompare(b))
}

/** What one output column is made from, as the edges say it (`transform` is the tool's word: `direct`, `expression`, `aggregate` and so on). */
export function sourcesOf(edges: readonly ColumnEdge[], model: string, column: string): ColumnEdge[] {
  return edges.filter((e) => e.toModel === model && e.toColumn === column)
}

/** A step as a line: `table.column`, with the number of steps when it is not the next one. */
export const stepLine = (s: ColumnStep): string => `${s.table}.${s.column}${s.distance > 1 ? ` (${s.distance} steps)` : ''}`

/** A source of a column as a line: the column it reads and how, or that it is a constant. */
export const sourceLine = (e: ColumnEdge): string => (e.fromTable === null || e.fromColumn === null ? `a constant (${e.transform})` : `${e.fromTable}.${e.fromColumn} (${e.transform})`)
