import { useMemo } from 'react'
import { connectionsOf, connectionPath, useDraft, useDraftFile, useDraftFiles, type DraftFile } from '@dbdatakit/workspace'
import { editTask, editText, mappingPath, mappingsOf, readConnection, readMapping, readTask, taskPath, type Read } from './model'
import type { ConnectionView, ReplicationTaskConfig, TableMappingConfig } from './types'

/** A file of the replication as a typed view: the value the screens edit, or why it cannot be shown here (it opens in Files). */
export interface FileView<T> {
  path: string
  value: T | null
  /** Why the Designer cannot show the file (it does not parse, or its format is newer than this reads); the file opens in Files. */
  problem: string | null
  loading: boolean
  dirty: boolean
  /** The file exists only as an unsaved edit. */
  isNew: boolean
}

function view<T>(file: DraftFile, isNew: boolean, read: (text: string) => Read<T>): FileView<T> {
  const r = file.text === undefined ? null : read(file.text)
  const problem = r?.problem ?? (file.error ? `${file.path} could not be read.` : null)
  return { path: file.path, value: r?.value ?? null, problem, loading: file.loading, dirty: file.dirty, isNew }
}

/** A problem of one file, for the list that links each to Files. */
export interface FileProblem { path: string; message: string }

export interface SyncDraft {
  replication: string
  task: FileView<ReplicationTaskConfig>
  /** Writes a changed task into the draft as an unsaved edit (only what changed; the file in the current format). */
  setTask: (next: ReplicationTaskConfig) => void
  mappingNames: string[]
  mappings: FileView<TableMappingConfig>[]
  mappingOf: (name: string) => FileView<TableMappingConfig> | undefined
  /** Writes a changed mapping into the draft as an unsaved edit. */
  setMapping: (name: string, next: TableMappingConfig) => void
  /** The set's connections, read only (they are edited in Files). */
  connections: ConnectionView[]
  /** Every file of the replication, and every connection, the Designer cannot show. */
  problems: FileProblem[]
}

/**
 * The draft model of one replication: its `task.yaml`, its table mappings and the set's connections as typed views over the frame's draft, unsaved
 * edits included. A screen changes a view and hands it back; the model writes each changed value into the file's text as a path edit, so comments
 * and keys no screen knows stay as they were. Nothing here talks to a server: the files come and go through the frame (`DbDataWorkspace/README.md`,
 * "A project's two areas: Files and Designer").
 */
export function useSyncDraft(replication: string): SyncDraft {
  const draft = useDraft()
  const tPath = taskPath(replication)
  const taskFile = useDraftFile(tPath)
  const mappingNames = useMemo(() => mappingsOf(draft.paths, replication), [draft.paths, replication])
  const mappingFiles = useDraftFiles(useMemo(() => mappingNames.map((m) => mappingPath(replication, m)), [mappingNames, replication]))
  const connectionNames = useMemo(() => connectionsOf(draft.paths), [draft.paths])
  const connectionFiles = useDraftFiles(useMemo(() => connectionNames.map(connectionPath), [connectionNames]))

  const task = view(taskFile, draft.isNew(tPath), (text) => readTask(tPath, replication, text))
  const mappings = mappingFiles.map((f) => view(f, draft.isNew(f.path), (text) => readMapping(f.path, text)))
  const connectionViews = connectionFiles.map((f) => view(f, false, (text) => readConnection(f.path, text)))
  const connections = connectionViews.flatMap((c) => (c.value ? [c.value] : []))
  const problems: FileProblem[] = [task, ...mappings, ...connectionViews].flatMap((v) => (v.problem ? [{ path: v.path, message: v.problem }] : []))

  const setTask = (next: ReplicationTaskConfig) => {
    if (taskFile.text === undefined || task.value === null) return
    const text = editTask(taskFile.text, task.value, next)
    if (text !== taskFile.text) draft.edit(tPath, text)
  }
  const mappingOf = (name: string) => mappings[mappingNames.indexOf(name)]
  const setMapping = (name: string, next: TableMappingConfig) => {
    const i = mappingNames.indexOf(name)
    const file = mappingFiles[i]
    const before = mappings[i]?.value
    if (!file || file.text === undefined || !before) return
    const text = editText(file.text, before, next)
    if (text !== file.text) draft.edit(file.path, text)
  }

  return { replication, task, setTask, mappingNames, mappings, mappingOf, setMapping, connections, problems }
}
