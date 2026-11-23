import type { TableMetadata } from './types'

/**
 * Whether a named table is one the catalog has, compared exactly as Sync's own lookup does. On an engine that folds case, `orders` where the
 * catalog says `Orders` reads as "not there yet" and provisioning then finds it: the harmless way to be wrong.
 * Undefined while the catalog is not known, so a caller can tell "not there" from "not known" and say neither too early.
 */
export function tableExists(tables: TableMetadata[] | undefined, schema: string, table: string): boolean | undefined {
  if (!table) return false
  if (!tables) return undefined
  return tables.some((t) => t.schema === schema && t.table === table)
}
