import type { EndpointRef, ResolvedRef, TableSpec } from './types'

/**
 * The endpoint a mapping side uses once the replication's is applied, as Sync resolves it (`EndpointResolution`): each field falls back on its own,
 * so a mapping can override only the database on the replication's connection.
 */
export function resolveSide(inherited: EndpointRef | null, spec: TableSpec): ResolvedRef {
  return {
    connectionName: spec.connectionName ?? inherited?.connectionName ?? '',
    database: spec.database ?? inherited?.database ?? '',
    schema: spec.schema,
    table: spec.table,
  }
}
