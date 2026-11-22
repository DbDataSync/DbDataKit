import { removeAt } from '@dbdatakit/shell'
import { writeColumns, type CacheConnection, type CacheTable } from '@dbdatakit/workspace'
import { findTable } from '../metadata'
import { mappingPath, newMappingText } from '../model'
import type { EndpointRef } from '../types'

/** What Create mappings could not do for one mapping, in the words of the side it is about. */
export interface CreateNote { mapping: string; side: 'source' | 'target'; reason: string }

export interface CreatedMappings { files: { path: string; text: string }[]; created: string[]; skipped: string[]; notes: CreateNote[] }

/**
 * The mapping files for the ticked tables of the replication's source: one per table, named `schema.table`, stating only its tables (the target
 * mirrors the source's schema and table) so everything else follows the replication. Each side's columns come from the catalog cache through the
 * mapping writer: the columns of the same name paired, the source's key as the key, and both column lists cached. A target the cache does not have
 * is one provisioning will create, so every source column is mapped and no target columns are cached. No target type is written: it is inferred
 * when the target is provisioned. A table that already has a mapping of its name is skipped.
 */
export function createMappings(replication: string, existing: readonly string[], tables: { schema: string; table: string }[],
  source: { endpoint: EndpointRef | null; connection: CacheConnection | undefined },
  target: { endpoint: EndpointRef | null; connection: CacheConnection | undefined }): CreatedMappings {
  const out: CreatedMappings = { files: [], created: [], skipped: [], notes: [] }
  const taken = new Set(existing.map((e) => e.toLowerCase()))
  for (const t of tables) {
    const name = t.schema ? `${t.schema}.${t.table}` : t.table
    if (taken.has(name.toLowerCase())) { out.skipped.push(name); continue }
    taken.add(name.toLowerCase())
    let text = newMappingText(name, t, t)
    const from: CacheTable | undefined = source.connection && findTable(source.connection, source.endpoint?.database ?? undefined, t.schema, t.table)
    const to: CacheTable | undefined = target.connection && findTable(target.connection, target.endpoint?.database ?? undefined, t.schema, t.table)
    if (!from) {
      out.notes.push({ mapping: name, side: 'source', reason: 'the source table is not in the catalog cache, so nothing was captured and no columns were mapped' })
    } else {
      const shape = to ?? from
      const sourceNames = new Set(from.columns.map((c) => c.name))
      const sourceOf = Object.fromEntries(shape.columns.filter((c) => sourceNames.has(c.name)).map((c) => [c.name, c.name]))
      const key = from.columns.filter((c) => c.key).map((c) => c.name)
      text = writeColumns(text, { sourceOf, key }, from, shape)
      if (!to) {
        text = removeAt(text, ['targetColumns'])
        out.notes.push({ mapping: name, side: 'target', reason: 'the target table is not in the catalog cache: it is created with these columns when the target is provisioned' })
      }
    }
    out.files.push({ path: mappingPath(replication, name), text })
    out.created.push(name)
  }
  return out
}
