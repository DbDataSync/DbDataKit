import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { workspaceApi, type FileList } from './api'

/** The query that holds a draft's findings for the overview and the chrome's count; every save, new file, move and delete invalidates it. */
export const findingsKey = (project: string, id: string) => ['workspace', 'findings', project, id] as const
export const fileKey = (project: string, id: string, path: string) => ['workspace', 'file', project, id, path] as const
export const filesKey = (project: string, id: string) => ['workspace', 'files', project, id] as const

/** One file of the draft as the screens see it: the text on the server, the text the person has typed and not saved, and which one is shown. */
export interface DraftFile {
  path: string
  /** The text shown: the unsaved edit when there is one, else what the server holds; undefined while it loads or when the file is neither. */
  text: string | undefined
  /** What the server holds, undefined for a file made in this browser and not saved yet. */
  saved: string | undefined
  dirty: boolean
  loading: boolean
  error: unknown
}

/**
 * A draft as both areas of a project edit it (Files and Designer, `DbDataWorkspace/README.md`): its files on the server, and the edits not saved yet,
 * which live here, in the frame, so an edit made in one area is there, still not saved, in the other. Nothing here is remembered beyond the page:
 * a reload drops what was not saved, as closing an editor always has.
 */
export interface Draft {
  project: string
  id: string
  kind: string | undefined
  /** Whether the person may change this draft (it is theirs and they hold the role). */
  canEdit: boolean
  /** Every path of the draft: those on the server and those made here and not saved yet, in path order. */
  paths: string[]
  /** The paths with an unsaved edit (a new file among them). */
  dirtyPaths: string[]
  /** Whether a path exists only as an unsaved edit. */
  isNew: (path: string) => boolean
  /** The text shown for a path, when it is known here (an unsaved edit, or a server text already read). */
  textOf: (path: string) => string | undefined
  /** Sets the unsaved text of a path (a new path makes a new file); a text equal to the server's is no longer an edit. */
  edit: (path: string, text: string) => void
  /** Drops the unsaved edit of a path (a new file disappears). */
  discard: (path: string) => void
  /** Moves an unsaved edit to another path (the file was moved on the server). */
  rename: (from: string, to: string) => void
  /** Saves one path's edit into the draft; resolves to the text the server now holds. */
  saveOne: (path: string) => Promise<string | undefined>
  /** Saves every unsaved edit. */
  saveAll: () => Promise<void>
  /** Deletes a file from the draft on the server (a file made here and not saved is only dropped); its unsaved edit goes with it. */
  remove: (path: string) => Promise<void>
  saving: boolean
  saveError: unknown
  /** The server's file list could not be read. */
  filesError: unknown
  filesLoading: boolean
  truncated: boolean
}

const DraftContext = createContext<Draft | null>(null)

/** The draft of the page; a component outside a project's frame has none and throws, so a Designer is never drawn without one. */
export function useDraft(): Draft {
  const d = useContext(DraftContext)
  if (!d) throw new Error('useDraft is used outside a draft frame.')
  return d
}

/** A draft for a component that may be drawn outside a frame (a test of a Files panel). */
export const useDraftOrNull = () => useContext(DraftContext)

/** One file of the draft, read from the server when it is there and not read yet; the unsaved edit wins. */
export function useDraftFile(path: string | null): DraftFile {
  const d = useDraft()
  const onServer = path !== null && !d.isNew(path) && !d.filesLoading
  const loaded = useQuery({ queryKey: fileKey(d.project, d.id, path ?? ''), queryFn: () => workspaceApi.readFile(d.project, d.id, path!), enabled: onServer, staleTime: Infinity, gcTime: 0, retry: false })
  const editText = path !== null && d.dirtyPaths.includes(path) ? d.textOf(path) : undefined
  const saved = onServer ? loaded.data : undefined
  return { path: path ?? '', text: editText ?? saved, saved, dirty: editText !== undefined, loading: onServer && loaded.isLoading, error: onServer ? loaded.error : null }
}

/** Several files at once (a Designer reads every mapping of a replication), each as `useDraftFile` gives it. */
export function useDraftFiles(paths: readonly string[]): DraftFile[] {
  const d = useDraft()
  const results = useQueries({
    queries: paths.map((path) => ({ queryKey: fileKey(d.project, d.id, path), queryFn: () => workspaceApi.readFile(d.project, d.id, path), enabled: !d.isNew(path) && !d.filesLoading, staleTime: Infinity, gcTime: 0, retry: false })),
  })
  return paths.map((path, i) => {
    const r = results[i]
    const onServer = !d.isNew(path)
    const editText = d.dirtyPaths.includes(path) ? d.textOf(path) : undefined
    const saved = onServer ? r.data : undefined
    return { path, text: editText ?? saved, saved, dirty: editText !== undefined, loading: onServer && r.isLoading, error: onServer ? r.error : null }
  })
}

/** Holds a draft's unsaved edits for both areas; `kind` and `canEdit` come from the frame, which knows the project and the person. */
export function DraftProvider({ project, id, kind, canEdit, enabled = true, children }: { project: string; id: string; kind: string | undefined; canEdit: boolean; enabled?: boolean; children: ReactNode }) {
  const client = useQueryClient()
  const [edits, setEdits] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<unknown>(null)
  const files = useQuery({ queryKey: filesKey(project, id), queryFn: () => workspaceApi.files(project, id), enabled })
  const serverPaths = useMemo(() => (files.data?.files ?? []).map((f) => f.path), [files.data])
  const onServer = useMemo(() => new Set(serverPaths), [serverPaths])
  const savedText = useCallback((path: string) => client.getQueryData<string>(fileKey(project, id, path)), [client, project, id])

  const edit = useCallback((path: string, text: string) => {
    setEdits((e) => {
      if (onServer.has(path) && savedText(path) === text) {
        if (!(path in e)) return e
        const { [path]: _same, ...rest } = e
        return rest
      }
      return e[path] === text ? e : { ...e, [path]: text }
    })
  }, [onServer, savedText])
  const discard = useCallback((path: string) => setEdits((e) => {
    if (!(path in e)) return e
    const { [path]: _gone, ...rest } = e
    return rest
  }), [])
  const rename = useCallback((from: string, to: string) => setEdits((e) => {
    if (!(from in e)) return e
    const { [from]: text, ...rest } = e
    return { ...rest, [to]: text }
  }), [])

  const afterSave = useCallback(async () => {
    await client.invalidateQueries({ queryKey: filesKey(project, id) })
    await client.invalidateQueries({ queryKey: findingsKey(project, id) })
    await client.invalidateQueries({ queryKey: ['workspace', 'changes', project, id] })
    await client.invalidateQueries({ queryKey: ['workspace', 'catalog', project, id] })
    // a replication's provisioning plan reads the saved files under the unsaved edits, so what a save wrote is planned again; a plan kept from
    // before the edits is dropped, so it is never shown as the plan of the saved files
    client.removeQueries({ queryKey: ['workspace', 'provisioning-plan', project, id], type: 'inactive' })
    await client.invalidateQueries({ queryKey: ['workspace', 'provisioning-plan', project, id] })
  }, [client, project, id])

  // a save sends the text of the moment; someone who typed on while it ran keeps what they typed
  const write = useCallback(async (path: string, text: string) => {
    await workspaceApi.writeFile(project, id, path, text)
    client.setQueryData(fileKey(project, id, path), text)
    // a new file joins the list of the server's files before its edit goes, so the draft never shows a moment without it (a screen open on it would
    // take it for removed and leave); the list read again after the save replaces this
    client.setQueryData<FileList>(filesKey(project, id), (list) => !list || list.files.some((f) => f.path === path) ? list
      : { ...list, files: [...list.files, { path, size: text.length }].sort((a, b) => a.path.localeCompare(b.path)) })
    setEdits((e) => (e[path] === text ? (({ [path]: _done, ...rest }) => rest)(e) : e))
  }, [client, project, id])

  const saveOne = useCallback(async (path: string) => {
    const text = edits[path]
    if (text === undefined) return savedText(path)
    setSaveError(null)
    try { await write(path, text) } catch (e) { setSaveError(e); throw e }
    await afterSave()
    return text
  }, [edits, write, afterSave, savedText])

  const saveAll = useCallback(async () => {
    setSaving(true)
    setSaveError(null)
    try {
      for (const [path, text] of Object.entries(edits)) await write(path, text)
    } catch (e) {
      setSaveError(e)
    } finally {
      setSaving(false)
      await afterSave()
    }
  }, [edits, write, afterSave])

  const remove = useCallback(async (path: string) => {
    if (onServer.has(path)) await workspaceApi.deleteFile(project, id, path)
    discard(path)
    client.removeQueries({ queryKey: fileKey(project, id, path) })
    await afterSave()
  }, [onServer, project, id, discard, client, afterSave])

  const draft = useMemo<Draft>(() => {
    const dirtyPaths = Object.keys(edits).sort()
    const paths = [...new Set([...serverPaths, ...dirtyPaths])].sort()
    return {
      project, id, kind, canEdit, paths, dirtyPaths,
      isNew: (path) => !onServer.has(path),
      textOf: (path) => edits[path] ?? savedText(path),
      edit, discard, rename, saveOne, saveAll, remove, saving, saveError,
      filesError: files.error, filesLoading: files.isLoading, truncated: files.data?.truncated ?? false,
    }
  }, [edits, serverPaths, onServer, project, id, kind, canEdit, savedText, edit, discard, rename, saveOne, saveAll, remove, saving, saveError, files.error, files.isLoading, files.data])

  return <DraftContext.Provider value={draft}>{children}</DraftContext.Provider>
}
