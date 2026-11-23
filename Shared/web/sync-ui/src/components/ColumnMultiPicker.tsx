/**
 * Picks any number of a mapping's columns, by their target names, as chips: what is chosen reads without opening anything. A chosen column the
 * mapping no longer has is kept and marked rather than dropped, since dropping it would be a change nobody made.
 */
export function ColumnMultiPicker({ columns, selected, onChange, testId, empty }: {
  columns: string[]
  selected: string[]
  onChange: (next: string[]) => void
  testId: string
  /** What to say when nothing is picked, since for these fields that is a meaningful answer. */
  empty: string
}) {
  const toggle = (column: string) => onChange(
    selected.includes(column) ? selected.filter((c) => c !== column) : [...selected, column])

  return (
    <div className="chips" data-testid={testId}>
      {columns.map((column) => (
        <button
          key={column}
          type="button"
          className={`chip ${selected.includes(column) ? 'on' : ''}`}
          aria-pressed={selected.includes(column)}
          onClick={() => toggle(column)}
          data-testid={`${testId}-${column}`}
        >
          {column}
        </button>
      ))}
      {selected.filter((c) => !columns.includes(c)).map((column) => (
        <button
          key={column}
          type="button"
          className="chip on unknown"
          title="This column is not in the mapping."
          onClick={() => toggle(column)}
          data-testid={`${testId}-${column}`}
        >
          {column} — not mapped
        </button>
      ))}
      {columns.length === 0 && <span className="hint">No columns mapped yet.</span>}
      {selected.length === 0 && columns.length > 0 && <span className="hint">{empty}</span>}
    </div>
  )
}
