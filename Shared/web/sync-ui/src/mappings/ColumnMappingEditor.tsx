import { useState } from 'react'
import { EditableValue } from '@dbdatakit/shell'
import { columnsOf, findTable, useCacheConnection } from '../metadata'
import type { ColumnMapping, ColumnMetadata, RelationshipConfig, ResolvedRef } from '../types'

interface Props {
  /** The source's endpoint: a relationship's table is on the same connection and database. */
  resolvedSource: ResolvedRef
  target: ResolvedRef
  mappings: ColumnMapping[]
  onChange: (mappings: ColumnMapping[]) => void
  /** False when the target names a table provisioning has yet to create; undefined while it is not known. */
  targetExists: boolean | undefined
  /** The source's columns, from the catalog cache (a query's, as its description recorded them in the mapping); undefined when they are not known. */
  sourceColumns: ColumnMetadata[] | undefined
  /** The target's columns, from the catalog cache; undefined when the cache does not have the table. */
  catalogTargetColumns: ColumnMetadata[] | undefined
  relationships: RelationshipConfig[]
  /** The target type each source column gets when nothing is set, by source column (`sync/inferred-columns`); absent while it is not known. */
  inferredTypes?: Record<string, string>
  /** Whether the source is a query: its columns are the mapping's own, from a description of the query, never the catalog cache's. */
  querySource?: boolean
}

const COLUMNS = '1fr 22px 1fr 0.95fr 1.15fr 74px'

/**
 * Column Mapping: once both sides name a table, the target's columns, each fed from a source column, with the source column's type beside it and
 * a PK badge on the target. A transform is SQL in the source's dialect, run by the source engine, with `{{column}}` for the column itself (the
 * reference is spelled differently in each reader's statement).
 *
 * When the target does not exist yet its columns are the source's: provisioning builds the `CREATE TABLE` from the column mappings, so what is
 * listed here is what gets created. A target type nobody set is shown as the type the Workspace infers from the source column's
 * (`sync/inferred-columns`, the code provisioning runs), and stays unset in the file, so it follows the source until a person sets one.
 */
export function ColumnMappingEditor({ resolvedSource, target, mappings, onChange, targetExists, sourceColumns, catalogTargetColumns, relationships, inferredTypes, querySource = false }: Props) {
  const targetColumns = targetExists === false ? sourceColumns : catalogTargetColumns
  const sourceConnection = useCacheConnection(resolvedSource.connectionName || undefined).data
  const relationshipColumnsByName: Record<string, ColumnMetadata[]> = {}
  for (const r of relationships) {
    const t = sourceConnection && r.table ? findTable(sourceConnection, resolvedSource.database || undefined, r.schema, r.table) : undefined
    relationshipColumnsByName[r.name] = t ? columnsOf(t) : []
  }
  const [columnToAdd, setColumnToAdd] = useState('')
  const [addProblem, setAddProblem] = useState<string | null>(null)

  if (!sourceColumns || !targetColumns || !target.table) {
    return (
      <div className="card" data-testid="column-mappings-table">
        <div className="card-head tight"><span className="card-title sm">Column mappings</span></div>
        <div className="empty" data-testid="column-mappings-waiting">
          {querySource && !sourceColumns
            ? 'The columns a query returns are not in the catalog cache: describe the query (Query columns, above) and its columns are listed here.'
            : !target.table || !resolvedSource.table
            ? 'Select a source and target table to map columns.'
            : 'The catalog cache does not have both tables: their columns are listed once their connections\' catalogs are read into the draft.'}
        </div>
      </div>
    )
  }

  // a column that comes through a relationship is looked up in that relationship's table, never in the source's own
  const columnsFor = (m: ColumnMapping) => (m.relationship ? (relationshipColumnsByName[m.relationship] ?? []) : sourceColumns)
  const typeOf = (m: ColumnMapping) => columnsFor(m).find((c) => c.name === m.sourceColumn)?.nativeType
  const isKnownColumn = (m: ColumnMapping) => columnsFor(m).some((c) => c.name === m.sourceColumn)
  // the option's value carries the relationship too, since its column may share a name with one of the source's
  const encodeOption = (relationship: string | null | undefined, column: string) => (relationship ? `${relationship}\u0000${column}` : column)
  const decodeOption = (value: string): { relationship: string | null; sourceColumn: string } => {
    const sep = value.indexOf('\u0000')
    return sep < 0 ? { relationship: null, sourceColumn: value } : { relationship: value.slice(0, sep), sourceColumn: value.slice(sep + 1) }
  }
  // a column through a relationship is not the source table's, so no type is inferred for it here
  const inferredOf = (m: ColumnMapping) => (m.relationship ? undefined : inferredTypes?.[m.sourceColumn])
  const isPk = (name: string) => targetColumns.find((c) => c.name === name)?.isPrimaryKey
  const mapped = new Set(mappings.map((m) => m.targetColumn))
  const unmapped = targetColumns.filter((tc) => !mapped.has(tc.name))

  const updateRow = (index: number, patch: Partial<ColumnMapping>) => onChange(mappings.map((m, i) => (i === index ? { ...m, ...patch } : m)))

  /**
   * Renaming a target column the target already has is recorded, so provisioning renames it and its rows keep their values (a drop and add would
   * empty it); renaming one the target does not have is a correction, with nothing to rename.
   */
  const renameTarget = (index: number, next: string | null) => {
    const from = mappings[index].targetColumn
    if (!next || next === from) return
    const onTarget = targetExists === true && targetColumns.some((c) => c.name === from)
    updateRow(index, { targetColumn: next, renames: onTarget ? [...(mappings[index].renames ?? []), { from, to: next, applied: false }] : mappings[index].renames })
  }

  /** Every target column the source also has, by name, one to one; Create mappings pairs the columns of a new mapping by the same rule. */
  const autoMap = () => {
    const sourceNames = new Set(sourceColumns.map((c) => c.name))
    onChange(targetColumns.filter((tc) => sourceNames.has(tc.name)).map((tc) => ({ sourceColumn: tc.name, targetColumn: tc.name, transform: null })))
  }

  /**
   * A row for a target column named by hand. It need not be on the target (provisioning adds it); it may not be a second row for a column this
   * mapping already writes, which would fail at staging naming neither row.
   */
  const addColumn = () => {
    const name = columnToAdd.trim()
    if (!name) { setAddProblem('Name the target column to add.'); return }
    if (mapped.has(name)) { setAddProblem(`'${name}' is already mapped — edit that row instead of adding a second one.`); return }
    const suggestion = sourceColumns.find((c) => c.name === name)?.name ?? sourceColumns[0]?.name ?? ''
    onChange([...mappings, { sourceColumn: suggestion, targetColumn: name, transform: null }])
    setColumnToAdd('')
    setAddProblem(null)
  }

  return (
    <div className="card flush" data-testid="column-mappings-table">
      <div className="card-head tight">
        <span className="card-title sm">Column mappings</span>
        <span className="card-note">
          {mappings.length} of {targetColumns.length} target columns mapped ·{' '}
          {targetExists === false
            ? <>the target does not exist yet, so these are the source's columns — they are what will be created</>
            : <>transforms are SQL in the source's dialect, with <code>{'{{column}}'}</code> for the column itself</>}
        </span>
        <button type="button" className="btn btn-sm spacer" onClick={autoMap} data-testid="auto-map-button">Auto-map by name</button>
      </div>

      <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 0, height: 29 }}>
        <span>Source column</span><span /><span>Target column</span><span>Target type</span><span>Transform</span><span />
      </div>

      {mappings.map((m, i) => (
        <div key={i} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 0 }} data-testid="column-mapping-row" data-target={m.targetColumn}>
          <span className="row" style={{ gap: 7 }}>
            <select
              className="select sm"
              style={{ maxWidth: 190 }}
              value={encodeOption(m.relationship, m.sourceColumn)}
              onChange={(e) => { const { relationship, sourceColumn } = decodeOption(e.target.value); updateRow(i, { sourceColumn, relationship }) }}
              data-testid={`column-mapping-source-${i}`}
            >
              {/* a stored value the cache does not have is shown as itself, marked: without it the browser would show the first option instead */}
              {!isKnownColumn(m) && (
                <option value={encodeOption(m.relationship, m.sourceColumn)}>
                  {!m.sourceColumn ? 'Select…' : m.relationship ? `${m.relationship} → ${m.sourceColumn} — not found` : `${m.sourceColumn} — not on the source`}
                </option>
              )}
              {sourceColumns.map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
              {relationships.map((r) => {
                const cols = relationshipColumnsByName[r.name] ?? []
                return cols.length === 0 ? null : (
                  <optgroup key={r.name} label={r.name}>
                    {cols.map((c) => <option key={c.name} value={encodeOption(r.name, c.name)}>{c.name}</option>)}
                  </optgroup>
                )
              })}
            </select>
            <span className="faint" data-testid={`column-mapping-source-type-${i}`}>{typeOf(m)}</span>
          </span>
          <span className="faint">→</span>
          <span className="row" style={{ gap: 7, minWidth: 0 }}>
            <EditableValue value={m.targetColumn} label={`${m.targetColumn} target column name`} onChange={(next) => renameTarget(i, next)} testId={`column-mapping-target-${i}`} />
            {isPk(m.targetColumn) && <span className="badge badge-accent">PK</span>}
            {(m.renames?.some((r) => !r.applied) ?? false) && (
              <span className="badge" title={`Will be renamed from ${m.renames![0].from} when provisioning is applied.`}>RENAMED</span>
            )}
            {targetExists === true && !targetColumns.some((c) => c.name === m.targetColumn) && (
              <span className="badge" title="Not on the target table yet — provisioning adds it when it is applied." data-testid={`column-mapping-will-add-${m.targetColumn}`}>WILL ADD</span>
            )}
          </span>
          <span>
            <EditableValue value={m.targetType ?? null} placeholder={inferredOf(m) ?? 'inferred'} label={`${m.targetColumn} target type`} onChange={(next) => updateRow(i, { targetType: next })} monospace testId={`column-mapping-type-${i}`} />
          </span>
          <span style={{ minWidth: 0 }}>
            <EditableValue value={m.transform ?? null} placeholder="none" label={`${m.targetColumn} transform`} onChange={(next) => updateRow(i, { transform: next })} monospace testId={`column-mapping-transform-${m.targetColumn}`} />
          </span>
          <button type="button" className="btn-link quiet" style={{ justifySelf: 'end' }} onClick={() => onChange(mappings.filter((_, index) => index !== i))} data-testid={`column-mapping-remove-${i}`}>
            Remove
          </button>
        </div>
      ))}

      {mappings.length === 0 && <div className="empty">No columns mapped yet.</div>}

      {/* always offered: a column the target lacks is one provisioning adds, and the row's badge says which of the two it turned out to be */}
      <div className="row" style={{ minHeight: 38, padding: '6px 14px', gap: 8, borderTop: '1px solid var(--row-edge)' }}>
        <input
          className="input sm"
          style={{ maxWidth: 240 }}
          list="add-target-column-options"
          placeholder="Add target column…"
          aria-label="Add target column"
          value={columnToAdd}
          onChange={(e) => { setColumnToAdd(e.target.value); setAddProblem(null) }}
          onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addColumn() } }}
          data-testid="add-target-column-input"
        />
        <datalist id="add-target-column-options">
          {unmapped.map((c) => <option key={c.name} value={c.name}>{c.isPrimaryKey ? 'PK' : ''}</option>)}
        </datalist>
        <button type="button" className="btn btn-sm" data-testid="add-target-column-button" onClick={addColumn}>Add</button>
        {addProblem && <span className="hint" data-testid="add-target-column-problem" style={{ color: 'var(--danger-ink)' }}>{addProblem}</span>}
      </div>
    </div>
  )
}
