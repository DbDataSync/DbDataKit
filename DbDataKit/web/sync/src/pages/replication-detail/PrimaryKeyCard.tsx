import type { ColumnMapping, ColumnMetadata } from '../../api/types'

/**
 * The columns that form this table's primary key, when the operator says so.
 *
 * Empty means "use what the source reports" — right for a table with a declared key. It is for the
 * sources that report none (a view, an ODBC driver, a Flight SQL proxy) that this exists: the key set
 * here is what the generated `CREATE TABLE` declares as `PRIMARY KEY` (as NOT NULL columns), what an
 * SCD Type 2 writer versions rows by, and what key reconcile matches on. Only columns the mapping
 * carries across are offered — a key column the target never receives can't be matched on.
 *
 * The order matters: it is the order of the index, so the chosen columns are listed in key order and
 * can be moved up and down. Columns are added at the end, in the order they are ticked.
 */
export function PrimaryKeyCard({ columnMappings, sourceColumns, value, onChange }: {
  columnMappings: ColumnMapping[]
  sourceColumns: ColumnMetadata[] | undefined
  value: string[]
  onChange: (next: string[]) => void
}) {
  const offered = columnMappings.map((m) => m.sourceColumn).filter(Boolean)
  const selected = new Set(value.map((v) => v.toLowerCase()))
  const reported = (sourceColumns ?? []).filter((c) => c.isPrimaryKey)
    .sort((a, b) => (a.keyOrdinal || 1e9) - (b.keyOrdinal || 1e9)).map((c) => c.name)

  const move = (index: number, by: -1 | 1) => {
    const next = [...value]
    ;[next[index], next[index + by]] = [next[index + by], next[index]]
    onChange(next)
  }

  const toggle = (name: string) =>
    onChange(selected.has(name.toLowerCase())
      ? value.filter((v) => v.toLowerCase() !== name.toLowerCase())
      : [...value, name])

  return (
    <div className="card" data-testid="primary-key-card">
      <div className="card-head">
        <span className="card-title">Primary key</span>
      </div>
      <div className="card-body" style={{ gap: 8 }}>
        <span className="hint" data-testid="primary-key-source">
          {reported.length > 0
            ? `The source reports a primary key: ${reported.join(', ')}.`
            : 'The source reports no primary key.'}
          {value.length === 0
            ? reported.length > 0 ? ' It is used as it is.' : ' Choose the columns below to give the target one.'
            : ' The columns chosen below replace it.'}
        </span>
        {offered.length === 0 && <span className="hint">Map some columns first.</span>}
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 16px' }}>
          {offered.map((name) => (
            <label key={name} className="row" style={{ gap: 6 }}>
              <input
                type="checkbox"
                checked={selected.has(name.toLowerCase())}
                onChange={() => toggle(name)}
                data-testid={`primary-key-column-${name}`}
              />
              <span className="mono">{name}</span>
            </label>
          ))}
        </div>
        {value.length > 1 && (
          <ol style={{ margin: 0, paddingLeft: 20 }} data-testid="primary-key-order">
            {value.map((name, i) => (
              <li key={name}>
                <span className="row" style={{ gap: 6 }}>
                  <span className="mono">{name}</span>
                  <button type="button" className="btn-link quiet" style={{ padding: 0 }} disabled={i === 0}
                    onClick={() => move(i, -1)} aria-label={`Move ${name} up`} data-testid={`primary-key-up-${name}`}>↑</button>
                  <button type="button" className="btn-link quiet" style={{ padding: 0 }} disabled={i === value.length - 1}
                    onClick={() => move(i, 1)} aria-label={`Move ${name} down`} data-testid={`primary-key-down-${name}`}>↓</button>
                </span>
              </li>
            ))}
          </ol>
        )}
        {value.length > 0 && (
          <div>
            <button type="button" className="btn-link quiet" style={{ padding: 0 }} onClick={() => onChange([])}
              data-testid="primary-key-clear">
              Use the source's own key
            </button>
          </div>
        )}
      </div>
    </div>
  )
}
