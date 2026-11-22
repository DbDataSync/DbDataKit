import { Field } from '@dbdatakit/shell'
import { EndpointSideCard, EndpointSidePair } from '../components/EndpointSidePair'
import { useDatabases, useReadLine } from '../metadata'
import type { ConnectionView, EndpointRef, TaskEndpoints } from '../types'

/**
 * The replication's source and target, which every table mapping takes unless it overrides them. The "inherited by N mappings" note is on both
 * cards, since a mapping overrides each side on its own. The connections are the draft's connection files; the databases are those the draft's
 * catalog cache lists for the connection, with where and when it was read.
 */
export function EndpointsCard({ endpoints, connections, mappingCount, onChange }: {
  endpoints: TaskEndpoints
  connections: ConnectionView[]
  mappingCount: number | undefined
  onChange: (next: TaskEndpoints) => void
}) {
  const note = mappingCount === undefined
    ? 'inherited by every table mapping'
    : `inherited by ${mappingCount === 1 ? 'the 1 mapping' : `all ${mappingCount} mappings`} unless overridden`

  return (
    <div data-testid="endpoints-card">
      <EndpointSidePair
        source={
          <EndpointSideCard side="source" title="Source" head={<span className="card-note">{note}</span>}>
            <EndpointFields value={endpoints.source} connections={connections} onChange={(source) => onChange({ ...endpoints, source })} testIdPrefix="task-source" />
          </EndpointSideCard>
        }
        target={
          <EndpointSideCard side="target" title="Target" head={<span className="card-note">{note}</span>}>
            <EndpointFields value={endpoints.target} connections={connections} onChange={(target) => onChange({ ...endpoints, target })} testIdPrefix="task-target" />
          </EndpointSideCard>
        }
      />
    </div>
  )
}

function EndpointFields({ value, connections, onChange, testIdPrefix }: {
  value: EndpointRef | null
  connections: ConnectionView[]
  onChange: (next: EndpointRef) => void
  testIdPrefix: string
}) {
  const connectionName = value?.connectionName ?? ''
  const database = value?.database ?? ''
  const databases = useDatabases(connectionName || undefined)
  const read = useReadLine(connectionName || undefined)
  const known = connections.some((c) => c.name === connectionName)

  return (
    <>
      <div className="form-grid">
        <Field label="Connection">
          <select
            className="select"
            value={connectionName}
            onChange={(e) => onChange({ ...value, connectionName: e.target.value || null, database: null })}
            data-testid={`${testIdPrefix}-connection-select`}
          >
            <option value="">Select…</option>
            {connectionName && !known && <option value={connectionName}>{connectionName} — not a connection of this draft</option>}
            {connections.map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}
          </select>
        </Field>
        <Field label="Database">
          <select
            className="select"
            value={database}
            disabled={!connectionName}
            onChange={(e) => onChange({ ...value, connectionName: connectionName || null, database: e.target.value || null })}
            data-testid={`${testIdPrefix}-database-select`}
          >
            <option value="">Select…</option>
            {database && !(databases.data ?? []).includes(database) && <option value={database}>{database} — not in the catalog cache</option>}
            {(databases.data ?? []).map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
        </Field>
      </div>
      {connectionName && (read
        ? <span className="hint" data-testid={`${testIdPrefix}-read`}>Catalog {read}.</span>
        : databases.problem && <span className="hint" data-testid={`${testIdPrefix}-no-cache`}>{databases.problem}</span>)}
    </>
  )
}
