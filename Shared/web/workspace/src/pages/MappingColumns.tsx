import { useState } from 'react'
import type { CacheRead, CacheTable } from '../catalogCache'
import { tableLabel } from '../catalogCache'
import { choicesOfMapping, findCachedTable, suggestPairs, tablesOfMapping, unknownSources, writeColumns, type ColumnChoices } from '../mappingColumns'

export interface MappingColumnsViewProps {
  source: CacheTable
  target: CacheTable
  choices: ColumnChoices
  more: boolean
  readOnly: boolean
  /** True when the mapping's own text already says what the choices say. */
  written: boolean
  onChoose: (targetColumn: string, sourceColumn: string) => void
  onKey: (targetColumn: string, on: boolean) => void
  onSuggest: () => void
  onWrite: () => void
}

/** The column mapping of a table pair, drawn from the cache: one row per target column with the source column it is fed from and whether it is part of the key. Controlled, so a test can draw it. */
export function MappingColumnsView(p: MappingColumnsViewProps) {
  const missing = unknownSources(p.choices, p.source.columns)
  return (
    <div data-testid="wsp-mapcols" data-written={p.written} style={{ marginTop: 12 }}>
      <div className="hint" style={{ marginBottom: 6 }}>Columns, from the catalog cache: {tableLabel(p.source)} feeds {tableLabel(p.target)}.{p.more ? ' The mapping names more tables; the first pair is shown.' : ''}</div>
      <table className="table" data-testid="wsp-mapcols-table">
        <thead><tr><th>Target column</th><th>Type</th><th>Fed from</th><th>Key</th></tr></thead>
        <tbody>
          {p.target.columns.map((c) => (
            <tr key={c.name} data-testid="wsp-mapcols-row" data-column={c.name}>
              <td className="mono">{c.name}</td>
              <td className="hint">{c.type}</td>
              <td>
                <select className="input" aria-label={`Source column of ${c.name}`} disabled={p.readOnly} value={p.choices.sourceOf[c.name] ?? ''} onChange={(e) => p.onChoose(c.name, e.target.value)} data-testid="wsp-mapcols-source">
                  <option value="">(not fed)</option>
                  {p.source.columns.map((s) => <option key={s.name} value={s.name}>{s.name} ({s.type})</option>)}
                </select>
              </td>
              <td><input type="checkbox" aria-label={`${c.name} is part of the key`} disabled={p.readOnly} checked={p.choices.key.includes(c.name)} onChange={(e) => p.onKey(c.name, e.target.checked)} data-testid="wsp-mapcols-key" /></td>
            </tr>
          ))}
        </tbody>
      </table>
      {missing.length > 0 && <div className="banner error" role="alert" style={{ marginTop: 6 }} data-testid="wsp-mapcols-unknown"><span className="mark">!</span><span>{missing.join(', ')}: the source column chosen is not in the cached source table.</span></div>}
      {p.choices.key.length === 0 && <div className="hint" style={{ marginTop: 6 }} data-testid="wsp-mapcols-nokey">No column is marked as the key.</div>}
      {!p.readOnly && (
        <div className="row" style={{ gap: 8, marginTop: 8 }}>
          <button type="button" className="btn" data-testid="wsp-mapcols-suggest" onClick={p.onSuggest}>Match by name</button>
          <button type="button" className="btn btn-primary" disabled={p.written || missing.length > 0} data-testid="wsp-mapcols-write" onClick={p.onWrite}>Write into the mapping</button>
          <span className="hint">Writes <code>columnMappings</code>, <code>primaryKey</code> and the column cache (<code>sourceColumns</code>, <code>targetColumns</code>); nothing else in the file changes.</span>
        </div>
      )}
    </div>
  )
}

/** What stands in place of the editor when there is nothing to pick from, and why. */
export function MappingColumnsNote({ why }: { why: string }) {
  return <div className="hint" style={{ marginTop: 12 }} data-testid="wsp-mapcols-why">{why}</div>
}

/** The columns of a mapping picked from the catalog cache (plan 33 SC22g, D20): no database is asked, the paired server's earlier answer is. */
export function MappingColumns({ mapping, catalog, readOnly, onApply }: { mapping: string; catalog: CacheRead | null; readOnly: boolean; onApply: (text: string) => void }) {
  const [edited, setEdited] = useState<ColumnChoices | null>(null)
  const tables = tablesOfMapping(mapping)
  if (!tables) return <MappingColumnsNote why="The mapping names no source and target table yet, so there are no columns to pick." />
  if (catalog === null) return <MappingColumnsNote why="There is no catalog cache in this draft (the Catalog tab says what it is), so the columns cannot be offered; they can be written by hand in the YAML tab." />
  if (!catalog.cache) return <MappingColumnsNote why="The catalog cache has findings (see the Catalog tab); fix it to pick columns from it." />
  const from = findCachedTable(catalog.cache, tables.source)
  const to = findCachedTable(catalog.cache, tables.target)
  if (!from.table || !to.table) return <MappingColumnsNote why={from.why ?? to.why ?? ''} />
  const saved = choicesOfMapping(mapping)
  const choices = edited ?? (Object.keys(saved.sourceOf).length > 0 || saved.key.length > 0 ? saved : { sourceOf: suggestPairs(from.table.columns, to.table.columns), key: to.table.columns.filter((c) => c.key).map((c) => c.name) })
  const written = edited === null && (Object.keys(saved.sourceOf).length > 0 || saved.key.length > 0)
  return (
    <MappingColumnsView
      source={from.table} target={to.table} choices={choices} more={tables.more} readOnly={readOnly} written={written}
      onChoose={(t, s) => setEdited({ ...choices, sourceOf: { ...choices.sourceOf, [t]: s } })}
      onKey={(t, on) => setEdited({ ...choices, key: on ? [...choices.key.filter((k) => k !== t), t] : choices.key.filter((k) => k !== t) })}
      onSuggest={() => setEdited({ ...choices, sourceOf: suggestPairs(from.table!.columns, to.table!.columns) })}
      onWrite={() => { onApply(writeColumns(mapping, choices, from.table!, to.table!)); setEdited(null) }}
    />
  )
}
