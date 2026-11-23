import { request } from '@dbdatakit/shell'
import type { BulkCreateAnswer, DriverCapabilities, InferredColumns, ScriptSlotInfo } from './types'

// The one module of the sync Designer that asks the Workspace server anything: its sync routes of a draft, answered in its own process from Sync's
// libraries with no database opened (Design: DbDataWorkspace/README.md#a-projects-two-areas-files-and-designer). Files are read and written through
// the frame's draft, databases are read through the console, and no Runner is asked from here.

/** The draft's unsaved edits a question carries, so the answer reads the draft as the person sees it: `catalog-cache.yaml`, files under `config/` and `drivers/<folder>/driver.yaml`. */
export type Unsaved = Record<string, string | null>

/** The paths whose unsaved text a question carries. */
export const isQuestionPath = (path: string): boolean =>
  path === 'catalog-cache.yaml' || path.startsWith('config/') || /^drivers\/[^/]+\/driver\.yaml$/.test(path)

/** The most edits a question carries (the route refuses more). */
export const MAX_UNSAVED_FILES = 500

/** The unsaved edits of the draft that a question carries, in path order, from the frame's dirty paths and their texts. */
export function unsavedOf(dirtyPaths: readonly string[], textOf: (path: string) => string | undefined): Unsaved {
  const out: Unsaved = {}
  for (const path of [...dirtyPaths].filter(isQuestionPath).sort().slice(0, MAX_UNSAVED_FILES)) {
    const text = textOf(path)
    if (text !== undefined) out[path] = text
  }
  return out
}

const enc = encodeURIComponent
const sync = (project: string, id: string) => `/api/v1/projects/${enc(project)}/workspaces/${enc(id)}/sync`
const post = (body: unknown): RequestInit => ({ method: 'POST', body: JSON.stringify(body) })
const withUnsaved = (body: Record<string, unknown>, unsaved: Unsaved) => (Object.keys(unsaved).length > 0 ? { ...body, unsaved } : body)

/** The Workspace's sync routes of a draft, through the console's relay as the signed-in person. */
export const syncApi = {
  /** The script slots a binding can name, each with the levels it binds at. */
  scriptSlots: (project: string, id: string) => request<{ slots: ScriptSlotInfo[] }>(`${sync(project, id)}/script-slots`).then((r) => r.slots),
  /** What a connection's driver offers; 404 with the reason for a driver not known offline. */
  capabilities: (project: string, id: string, connection: string, unsaved: Unsaved = {}) =>
    request<DriverCapabilities>(`${sync(project, id)}/capabilities`, post(withUnsaved({ connection }, unsaved))),
  /** The target types and natural key one mapping would get from its source's columns in the catalog cache. */
  inferredColumns: (project: string, id: string, replication: string, mapping: string, unsaved: Unsaved = {}) =>
    request<InferredColumns>(`${sync(project, id)}/inferred-columns`, post(withUnsaved({ replication, mapping }, unsaved))),
  /** The mapping files Create mappings would make for the tables, one per table; nothing is written into the draft. */
  bulkCreate: (project: string, id: string, replication: string, tables: { schema: string; table: string }[], unsaved: Unsaved = {}) =>
    request<BulkCreateAnswer>(`${sync(project, id)}/bulk-create`, post(withUnsaved({ replication, tables }, unsaved))),
}
