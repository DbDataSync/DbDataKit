import { useState } from 'react'
import { Field } from '@dbdatakit/shell'
import { useDraft } from '@dbdatakit/workspace'
import { EndpointSideCard, EndpointSidePair } from '../components/EndpointSidePair'
import { ConnectionTestPanel } from '../components/ChannelReads'
import { RefreshPanel } from '../components/RefreshPanel'
import { useDatabases, useReadLine } from '../metadata'
import type { ConnectionView, EndpointRef, TaskEndpoints } from '../types'

/**
 * The replication's source and target, which every table mapping takes unless it overrides them. The "inherited by N mappings" note is on both
 * cards, since a mapping overrides each side on its own. The connections are the draft's connection files; the databases are those the draft's
 * catalog cache lists for the connection, with where and when it was read. A connection of the draft that the cache has nothing for offers
 * **Refresh catalog**: its shape read from a Runner channel, the difference shown, and accepted into the cache as an unsaved edit, which fills the
 * database picker. **Test** on each card runs the Runner's binding test of the connection in a channel picked there, through the console, with no build
 * and no row; the picker offers **Set up for development** where a channel does not bind the connection's slot, production as any other.
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
  const draft = useDraft()
  const [refreshing, setRefreshing] = useState(false)
  const [testing, setTesting] = useState(false)
  const uncached = connectionName !== '' && !read && databases.problem !== null

  return (
    <>
      <div className="form-grid">
        <Field label="Connection">
          <select
            className="select"
            value={connectionName}
            onChange={(e) => { setRefreshing(false); setTesting(false); onChange({ ...value, connectionName: e.target.value || null, database: null }) }}
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
      {uncached && known && draft.canEdit && (refreshing
        ? <RefreshPanel key={connectionName} connection={connectionName} onClose={() => setRefreshing(false)} testId={`${testIdPrefix}-refresh`} />
        : (
          <div>
            <button type="button" className="btn btn-sm" style={{ marginTop: 6 }} onClick={() => setRefreshing(true)} data-testid={`${testIdPrefix}-refresh-catalog`}>
              Refresh catalog
            </button>
          </div>
        ))}
      {connectionName && known && (testing
        ? <ConnectionTestPanel key={connectionName} connection={connectionName} onClose={() => setTesting(false)} testId={`${testIdPrefix}-test`} />
        : (
          <div>
            <button type="button" className="btn btn-sm" style={{ marginTop: 6 }} onClick={() => setTesting(true)} data-testid={`${testIdPrefix}-test-button`}
              title={`Test ${connectionName} on a Runner, in a channel that binds its slot`}>Test</button>
          </div>
        ))}
    </>
  )
}
