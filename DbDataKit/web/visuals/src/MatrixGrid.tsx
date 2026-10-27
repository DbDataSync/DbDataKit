import type { RenderedMatrix } from './types'

/** Header cells for one level: adjacent columns that share every label down to this level are one cell, so two measures under one quarter read as a quarter over two columns. */
function headerCells(columns: RenderedMatrix['columns'], level: number) {
  const cells: Array<{ label: string; span: number; first: number }> = []
  columns.forEach((column, i) => {
    const previous = cells[cells.length - 1]
    const same = previous && columns[previous.first]!.labels.slice(0, level + 1).join('\u0000') === column.labels.slice(0, level + 1).join('\u0000')
    // A level the column does not have (no column attribute) is an empty cell, not a repeat of the one above.
    if (same) previous.span += 1
    else cells.push({ label: column.labels[level] ?? '', span: 1, first: i })
  })
  return cells
}

/**
 * A matrix view as a grid: row labels down the left, column labels across the top (outermost first), the server's text in every cell. Nothing is computed here: a
 * total row or column is the one the result held (`total` on the row or column), Other is a sum the server made, and a cell reads as the server formatted it.
 */
export function MatrixGrid({ view }: { view: RenderedMatrix }) {
  const levels = Math.max(1, ...view.columns.map((c) => c.labels.length))
  const rowLabelWidth = Math.max(1, ...view.rows.map((r) => r.labels.length))
  return (
    <div className="vz-matrix-scroll">
      <table className="vz-matrix" data-testid={`vz-matrix-${view.id}`}>
        <thead>
          {Array.from({ length: levels }, (_, level) => (
            <tr key={level}>
              {level === 0 && <td className="vz-corner" colSpan={rowLabelWidth} rowSpan={levels} />}
              {headerCells(view.columns, level).map((cell) => (
                <th key={cell.first} scope="col" colSpan={cell.span} className={view.columns[cell.first]!.total ? 'vz-total' : view.columns[cell.first]!.other ? 'vz-other' : undefined}>
                  {cell.label}
                </th>
              ))}
            </tr>
          ))}
        </thead>
        <tbody>
          {view.rows.map((row, r) => (
            <tr key={r} className={row.total ? 'vz-total' : undefined}>
              {Array.from({ length: rowLabelWidth }, (_, i) => (
                <th key={i} scope="row">{row.labels[i] ?? ''}</th>
              ))}
              {row.cells.map((cell, c) => (
                <td key={c} className={['vz-num', view.columns[c]?.total ? 'vz-total' : '', view.columns[c]?.other ? 'vz-other' : ''].filter(Boolean).join(' ')}>{cell.text}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {view.notice ? <p className="vz-notice" role="note">{view.notice}</p> : null}
    </div>
  )
}
