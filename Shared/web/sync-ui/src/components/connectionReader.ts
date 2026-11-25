import { connectionPath, useDraft, useDraftFile, type CatalogReader } from '@dbdatakit/workspace'
import { readConnection } from '../model'
import type { ConnectionView } from '../types'

/** The engines the Runner's built-in package `dbdatakit-metadata` opens: a connection on another engine is read by the set's own package. */
export const BUILT_IN_READER_ENGINES: readonly string[] = ['sqlserver', 'postgres', 'mysql', 'mariadb', 'oracle']

/** Who reads a connection of the set on a Runner, and why in words when it is the set's own package (null for the built-in one). */
export interface ConnectionReader { reader: CatalogReader; why: string | null }

/**
 * Who reads a connection's shape: the Runner's built-in package for a connection on an engine it opens, the set's own package (its tasks `discover` and
 * `describe`, Sync's metadata service behind them) for a connection whose driver is Sync's own (a YAML driver, a driver the set carries, DuckDB, Flight SQL)
 * or whose `metadataProvider` script answers its catalog. A connection file that is not known yet is read by the built-in package.
 * Design: docs/authoring-metadata.md#a-sync-project
 */
export function connectionReader(view: ConnectionView | null | undefined): ConnectionReader {
  if (!view) return { reader: 'builtin', why: null }
  if (view.scripts.metadataProvider) {
    return { reader: 'package', why: `${view.name}'s metadataProvider script answers its catalog, so this set's own package reads it, in the build the channel holds.` }
  }
  if (!BUILT_IN_READER_ENGINES.includes(view.engine)) {
    return { reader: 'package', why: `${view.name} is a ${view.engine || 'custom'} connection, whose driver is Sync's own, so this set's own package reads it, in the build the channel holds.` }
  }
  return { reader: 'builtin', why: null }
}

/** Who reads a connection of the draft, from its file as the draft has it (unsaved edits included). */
export function useConnectionReader(connection: string): ConnectionReader {
  const paths = useDraft().paths
  const path = [connectionPath(connection), connectionPath(connection).replace(/\.yaml$/, '.yml')].find((p) => paths.includes(p)) ?? null
  const file = useDraftFile(path)
  const view = file.text === undefined ? null : readConnection(file.path, file.text).value
  return connectionReader(view)
}
