import { Field } from '@dbdatakit/shell'
import { useColumns, useTables } from '../metadata'
import type { ColumnMetadata, RelationshipConfig, RelationshipJoinKey, ResolvedRef, TableMetadata } from '../types'

/**
 * Named joins from the mapping's source table to another table on the same connection and database, for a column mapping to take a value
 * through. The table is picked from the catalog cache the source's own picker reads, and its columns come from the same cache. A join to the
 * source table itself is an ordinary pick.
 */
export function RelationshipsCard({ resolvedSource, sourceColumns, relationships, onChange }: {
  resolvedSource: ResolvedRef
  /** The source table's columns, for a join key's local column. */
  sourceColumns: ColumnMetadata[] | undefined
  relationships: RelationshipConfig[]
  onChange: (next: RelationshipConfig[]) => void
}) {
  const tables = useTables(resolvedSource.connectionName || undefined, resolvedSource.database || undefined)

  const replace = (index: number, next: RelationshipConfig) => onChange(relationships.map((r, i) => (i === index ? next : r)))
  const remove = (index: number) => onChange(relationships.filter((_, i) => i !== index))
  const addRelationship = () => {
    const existingNames = new Set(relationships.map((r) => r.name))
    let n = relationships.length + 1
    while (existingNames.has(`relationship${n}`)) n++
    onChange([...relationships, { name: `relationship${n}`, schema: '', table: '', joinKeys: [{ localColumn: '', foreignColumn: '' }] }])
  }

  return (
    <div className="card flush" data-testid="relationships-card">
      <div className="card-head tight">
        <span className="card-title sm">Relationships</span>
        <span className="card-note">named joins to other tables on the same connection, for a column mapping to pull a value through</span>
        <button type="button" className="btn btn-sm spacer" onClick={addRelationship} data-testid="add-relationship-button">Add relationship</button>
      </div>
      {relationships.length === 0 && <div className="empty">No relationships declared.</div>}
      {relationships.map((relationship, i) => (
        <RelationshipRow key={i} resolvedSource={resolvedSource} sourceColumns={sourceColumns} tables={tables.data ?? []} relationship={relationship}
          onChange={(next) => replace(i, next)} onRemove={() => remove(i)} testIndex={i} />
      ))}
    </div>
  )
}

function RelationshipRow({ resolvedSource, sourceColumns, tables, relationship, onChange, onRemove, testIndex }: {
  resolvedSource: ResolvedRef
  sourceColumns: ColumnMetadata[] | undefined
  tables: TableMetadata[]
  relationship: RelationshipConfig
  onChange: (next: RelationshipConfig) => void
  onRemove: () => void
  testIndex: number
}) {
  const foreignColumns = useColumns(resolvedSource.connectionName || undefined, resolvedSource.database || undefined, relationship.schema || undefined, relationship.table || undefined).data
  const selectedTableIndex = tables.findIndex((t) => t.schema === relationship.schema && t.table === relationship.table)
  const setTable = (index: number) => {
    const picked = tables[index]
    onChange(picked ? { ...relationship, schema: picked.schema, table: picked.table } : { ...relationship, schema: '', table: '' })
  }
  const replaceJoinKey = (index: number, next: RelationshipJoinKey) => onChange({ ...relationship, joinKeys: relationship.joinKeys.map((k, i) => (i === index ? next : k)) })
  const removeJoinKey = (index: number) => onChange({ ...relationship, joinKeys: relationship.joinKeys.filter((_, i) => i !== index) })
  const addJoinKey = () => onChange({ ...relationship, joinKeys: [...relationship.joinKeys, { localColumn: '', foreignColumn: '' }] })

  return (
    // `.auto`: this row stacks two lines and grows, where a plain grid row has a fixed height that would clip the second
    <div className="grid-row auto" style={{ display: 'flex', flexDirection: 'column', gap: 8, padding: '10px 14px' }}>
      <div className="row" style={{ gap: 8, alignItems: 'flex-end' }}>
        <Field label="Name">
          <input className="input sm" style={{ maxWidth: 160 }} value={relationship.name} onChange={(e) => onChange({ ...relationship, name: e.target.value })} data-testid={`relationship-name-${testIndex}`} />
        </Field>
        <Field label="Foreign table">
          <select className="select sm" value={selectedTableIndex < 0 ? '' : String(selectedTableIndex)} onChange={(e) => setTable(Number(e.target.value))} data-testid={`relationship-table-${testIndex}`}>
            <option value="">Select…</option>
            {tables.map((t, i) => <option key={i} value={i}>{t.schema ? `${t.schema}.` : ''}{t.table}</option>)}
          </select>
        </Field>
        <button type="button" className="btn-link quiet spacer" onClick={onRemove} data-testid={`remove-relationship-${testIndex}`}>Remove</button>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <span className="hint">Join keys, all of which must match</span>
        {relationship.joinKeys.map((key, ki) => (
          <div key={ki} className="row" style={{ gap: 8, alignItems: 'flex-end' }}>
            <Field label="Local column">
              <select className="select sm" value={key.localColumn} onChange={(e) => replaceJoinKey(ki, { ...key, localColumn: e.target.value })} data-testid={`relationship-${testIndex}-join-local-${ki}`}>
                <option value="">Select…</option>
                {(sourceColumns ?? []).map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
              </select>
            </Field>
            <span className="faint">=</span>
            <Field label="Foreign column">
              <select className="select sm" value={key.foreignColumn} disabled={!relationship.table} onChange={(e) => replaceJoinKey(ki, { ...key, foreignColumn: e.target.value })} data-testid={`relationship-${testIndex}-join-foreign-${ki}`}>
                <option value="">Select…</option>
                {(foreignColumns ?? []).map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
              </select>
            </Field>
            {relationship.joinKeys.length > 1 && (
              <button type="button" className="btn-link quiet" onClick={() => removeJoinKey(ki)} data-testid={`remove-relationship-${testIndex}-join-${ki}`}>Remove</button>
            )}
          </div>
        ))}
        <button type="button" className="btn btn-sm" style={{ alignSelf: 'flex-start' }} onClick={addJoinKey} data-testid={`add-relationship-${testIndex}-join`}>Add join key</button>
      </div>
    </div>
  )
}
