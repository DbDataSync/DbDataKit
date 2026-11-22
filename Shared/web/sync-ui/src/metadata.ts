import { useMemo } from 'react'
import { CATALOG_CACHE_FILE, readCatalogCache, readLine, useDraftFile, type CacheConnection, type CacheTable, type CatalogCache } from '@dbdatakit/workspace'
import type { ColumnMetadata, TableMetadata } from './types'

/** The draft's catalog cache as the screens read it: the cache, or why there is none to read. */
export interface CatalogState {
  cache: CatalogCache | null
  loading: boolean
  /** Why the cache cannot be read (it is not there, or its findings), for a line that links the file in Files; null when it reads. */
  problem: string | null
}

/**
 * The draft's `catalog-cache.yaml`, read in the browser from the frame's draft, so a refresh not yet saved counts. No server answers metadata:
 * the databases, tables and columns of the pickers are what the cache records (`docs/authoring-metadata.md`, "A sync project").
 */
export function useCatalog(): CatalogState {
  const file = useDraftFile(CATALOG_CACHE_FILE)
  return useMemo(() => {
    if (file.loading) return { cache: null, loading: true, problem: null }
    if (file.text === undefined) return { cache: null, loading: false, problem: `The draft has no ${CATALOG_CACHE_FILE} yet: the databases, tables and columns are listed once a connection's catalog is read into it.` }
    const read = readCatalogCache(file.text)
    if (read.cache === null) return { cache: null, loading: false, problem: `${CATALOG_CACHE_FILE} cannot be read: ${read.findings[0]?.message ?? 'it is not a catalog cache'}` }
    return { cache: read.cache, loading: false, problem: null }
  }, [file.loading, file.text])
}

/** A metadata answer: the list once the cache answers it, undefined while it loads or when the cache has nothing for it (`problem` says which). */
export interface MetadataAnswer<T> { data: T | undefined; problem: string | null }

function connectionOf(catalog: CatalogState, name: string | undefined): MetadataAnswer<CacheConnection> {
  if (!name) return { data: undefined, problem: null }
  if (catalog.cache === null) return { data: undefined, problem: catalog.problem }
  const connection = catalog.cache.connections[name]
  return connection ? { data: connection, problem: null } : { data: undefined, problem: `${CATALOG_CACHE_FILE} has nothing for the connection ${name} yet.` }
}

/** The tables of a connection in a database: every table when the connection lists no databases (an engine without them). */
export const tablesIn = (connection: CacheConnection, database: string | undefined): CacheTable[] =>
  connection.databases.length === 0 || !database ? connection.tables : connection.tables.filter((t) => t.database === database)

/** The columns of a cached table as the column pickers show them; a key column's place in the key is its place among the key columns. */
export function columnsOf(table: CacheTable): ColumnMetadata[] {
  let ordinal = 0
  return table.columns.map((c) => ({ name: c.name, nativeType: c.type, isNullable: c.nullable, isPrimaryKey: c.key, ...(c.key ? { keyOrdinal: ++ordinal } : {}), isIdentity: false }))
}

/** The cached table of a connection by schema and name, in a database when the connection lists them. */
export function findTable(connection: CacheConnection, database: string | undefined, schema: string, table: string): CacheTable | undefined {
  return tablesIn(connection, database).find((t) => t.schema === schema && t.name === table)
}

/** Where and when a connection was read, in words, or null when the cache has nothing for it. */
export function useReadLine(connectionName: string | undefined): string | null {
  const c = connectionOf(useCatalog(), connectionName).data
  return c?.read ? readLine(c.read) : null
}

/** The databases a connection has, from the cache. */
export function useDatabases(connectionName: string | undefined): MetadataAnswer<string[]> {
  const c = connectionOf(useCatalog(), connectionName)
  return c.data ? { data: c.data.databases.map((d) => d.name), problem: null } : { data: undefined, problem: c.problem }
}

/** The tables of a connection's database, from the cache. */
export function useTables(connectionName: string | undefined, database: string | undefined): MetadataAnswer<TableMetadata[]> {
  const c = connectionOf(useCatalog(), connectionName)
  return c.data ? { data: tablesIn(c.data, database).map((t) => ({ schema: t.schema, table: t.name })), problem: null } : { data: undefined, problem: c.problem }
}

/** The columns of a table, from the cache; undefined for a table the cache does not have. */
export function useColumns(connectionName: string | undefined, database: string | undefined, schema: string | undefined, table: string | undefined): MetadataAnswer<ColumnMetadata[]> {
  const c = connectionOf(useCatalog(), connectionName)
  if (!c.data || !table) return { data: undefined, problem: c.problem }
  const found = findTable(c.data, database, schema ?? '', table)
  return found ? { data: columnsOf(found), problem: null } : { data: undefined, problem: `${CATALOG_CACHE_FILE} has no table ${schema ? `${schema}.` : ''}${table} on ${connectionName}.` }
}

/** A connection's whole entry in the cache (its databases, tables and where it was read), for a screen that works on many tables at once. */
export function useCacheConnection(connectionName: string | undefined): MetadataAnswer<CacheConnection> {
  return connectionOf(useCatalog(), connectionName)
}
