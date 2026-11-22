import { Field } from '@dbdatakit/shell'
import { EndpointSideCard } from '../components/EndpointSidePair'
import { useDatabases, useTables } from '../metadata'
import { tableExists } from '../tableExists'
import type { ConnectionView, EndpointRef, TableSpec } from '../types'

interface Props {
  side: 'source' | 'target'
  label: string
  /** The replication's endpoint for this side: what a mapping that does not override uses. */
  inherited: EndpointRef | null
  spec: TableSpec
  connections: ConnectionView[]
  onChange: (next: TableSpec) => void
  testIdPrefix: string
  /**
   * The target side only: lets a person name a table the database does not have yet, so provisioning can create it. Not on the source, where a
   * name the catalog does not have is a typo: there is no creating a table to read from.
   */
  allowNewTable?: boolean
}

const readOnly = { display: 'flex', alignItems: 'center', background: 'var(--sunken)', borderColor: 'var(--chrome-edge)', color: 'var(--ink-3)' } as const

/**
 * One side of a table mapping: where it reads from or writes to, and which table. The endpoint is the replication's unless this mapping overrides
 * it (**Override for this table**: off shows the inherited connection and database read only, on makes them pickers). The table picker lists the
 * tables the draft's catalog cache has for the resolved endpoint, whichever way the toggle is.
 */
export function MappingSide({ side, label, inherited, spec, connections, onChange, testIdPrefix, allowNewTable = false }: Props) {
  const overriding = spec.connectionName !== null || spec.database !== null
  const connectionName = spec.connectionName ?? inherited?.connectionName ?? ''
  const database = spec.database ?? inherited?.database ?? ''

  const databases = useDatabases(connectionName || undefined)
  const tables = useTables(connectionName || undefined, database || undefined)
  const list = tables.data ?? []

  // the choice is a position in the list, never a joined "schema.table" string, which a name with a dot in it would split wrongly
  const selectedTableIndex = list.findIndex((t) => t.schema === spec.schema && t.table === spec.table)
  const exists = tableExists(tables.data, spec.schema, spec.table)
  const schemas = [...new Set(list.map((t) => t.schema))]
  const tablesInSchema = list.filter((t) => t.schema === spec.schema)

  // picking a name brings its schema with it, when only one schema has a table of that name
  const setTableName = (table: string) => {
    const matches = list.filter((t) => t.table === table)
    const schema = matches.length === 1 ? matches[0].schema : spec.schema
    onChange({ ...spec, schema, table })
  }

  // turning the override on starts from what is in effect; off goes back to inheriting
  const toggleOverride = () =>
    onChange(overriding ? { ...spec, connectionName: null, database: null } : { ...spec, connectionName: connectionName || null, database: database || null })

  return (
    <EndpointSideCard
      side={side}
      title={label}
      testId={`${testIdPrefix}-side`}
      head={
        <>
          {!overriding && <span className="badge">INHERITED</span>}
          <span className="spacer row" style={{ gap: 7 }}>
            <button type="button" className={`toggle ${overriding ? 'on' : ''}`} onClick={toggleOverride} aria-pressed={overriding} data-testid={`${testIdPrefix}-override-toggle`} />
            <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Override for this table</span>
          </span>
        </>
      }
    >
      <div className="form-grid">
        <Field label="Connection">
          {overriding ? (
            <select className="select" value={connectionName} onChange={(e) => onChange({ ...spec, connectionName: e.target.value || null, database: null, schema: '', table: '' })} data-testid={`${testIdPrefix}-connection-select`}>
              <option value="">Select…</option>
              {connections.map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
            </select>
          ) : (
            <span className="input" style={readOnly} data-testid={`${testIdPrefix}-connection`}>{connectionName || '—'}</span>
          )}
        </Field>

        <Field label="Database">
          {overriding ? (
            <select className="select" value={database} disabled={!connectionName} onChange={(e) => onChange({ ...spec, connectionName: connectionName || null, database: e.target.value || null, schema: '', table: '' })} data-testid={`${testIdPrefix}-database-select`}>
              <option value="">Select…</option>
              {database && !(databases.data ?? []).includes(database) && <option value={database}>{database}</option>}
              {(databases.data ?? []).map((d) => <option key={d} value={d}>{d}</option>)}
            </select>
          ) : (
            <span className="input" style={readOnly} data-testid={`${testIdPrefix}-database`}>{database || '—'}</span>
          )}
        </Field>
      </div>

      {allowNewTable ? (
        <>
          <div className="form-grid">
            <Field label="Schema">
              <input className="input" list={`${testIdPrefix}-schema-options`} value={spec.schema} disabled={!connectionName} placeholder="dbo" onChange={(e) => onChange({ ...spec, schema: e.target.value })} data-testid={`${testIdPrefix}-schema-input`} />
              <datalist id={`${testIdPrefix}-schema-options`}>{schemas.map((s) => <option key={s} value={s} />)}</datalist>
            </Field>
            <Field label="Table">
              <input className="input" list={`${testIdPrefix}-table-options`} value={spec.table} disabled={!connectionName} onChange={(e) => setTableName(e.target.value)} data-testid={`${testIdPrefix}-table-input`} />
              <datalist id={`${testIdPrefix}-table-options`}>{tablesInSchema.map((t) => <option key={t.table} value={t.table} />)}</datalist>
            </Field>
          </div>
          {exists === false && (
            <div className="hint" data-testid={`${testIdPrefix}-table-will-be-created`}>
              <span className="badge badge-accent">NEW</span> not in the catalog cache: provisioning creates it
            </div>
          )}
        </>
      ) : (
        <Field label="Table">
          <select
            className="select"
            value={selectedTableIndex < 0 ? (spec.table ? 'current' : '') : String(selectedTableIndex)}
            disabled={!connectionName}
            onChange={(e) => {
              const picked = list[Number(e.target.value)]
              if (e.target.value === 'current') return
              onChange(picked ? { ...spec, schema: picked.schema, table: picked.table } : { ...spec, schema: '', table: '' })
            }}
            data-testid={`${testIdPrefix}-table-select`}
          >
            <option value="">Select…</option>
            {selectedTableIndex < 0 && spec.table && <option value="current">{spec.schema ? `${spec.schema}.` : ''}{spec.table} — not in the catalog cache</option>}
            {list.map((t, i) => <option key={i} value={String(i)}>{t.schema ? `${t.schema}.` : ''}{t.table}</option>)}
          </select>
        </Field>
      )}
      {connectionName && tables.problem && <span className="hint" data-testid={`${testIdPrefix}-no-cache`}>{tables.problem}</span>}
    </EndpointSideCard>
  )
}
