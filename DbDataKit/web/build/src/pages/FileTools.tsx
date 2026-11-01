import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { CommandAnswer } from '../types'

/** Why a path cannot be a file of the project, in words; null when it can. The server confines every path to the project's tree all the same. */
export function filePathProblem(path: string): string | null {
  if (path === '') return null
  if (path.startsWith('/') || /^[A-Za-z]:/.test(path) || path.includes('\\')) return 'A path is relative to the project and uses /, for example connections/postgres.yaml.'
  if (path.split('/').some((part) => part === '' || part === '.' || part === '..')) return 'A path has no empty, . or .. part.'
  if (path.endsWith('/')) return 'A path names a file, not a folder.'
  return null
}

/** The first thing the tool said was wrong, in its own words, for a command that did not do it; null when it did. */
export function fileFailure(answer: CommandAnswer<unknown>, fallback: string): string | null {
  if (answer.document?.ok) return null
  return answer.document?.diagnostics?.find((d) => d.severity === 'error')?.found ?? answer.document?.errors?.[0] ?? (answer.text ? answer.text.split('\n')[0] : null) ?? fallback
}

export interface NewFileViewProps {
  path: string
  busy: boolean
  failure: string | null
  onPath: (path: string) => void
  onCreate: () => void
  onCancel: () => void
}

/** A path for a file that does not exist yet. It starts empty; the editor takes it from there. */
export function NewFileView(p: NewFileViewProps) {
  const problem = filePathProblem(p.path)
  const ready = p.path !== '' && problem === null && !p.busy
  return (
    <div className="card" style={{ marginBottom: 14 }} data-testid="build-new-file">
      <div className="card-body">
        <div className="row" style={{ gap: 12, flexWrap: 'wrap', alignItems: 'baseline' }}>
          <input className="mono" value={p.path} onChange={(e) => p.onPath(e.target.value)} placeholder="folder/name.yml" aria-label="Path of the new file" data-testid="build-new-file-path" style={{ minWidth: 280 }} />
          <button type="button" className="btn btn-primary" disabled={!ready} onClick={p.onCreate} data-testid="build-new-file-create">{p.busy ? 'Making…' : 'Make the file'}</button>
          <button type="button" className="btn" onClick={p.onCancel} data-testid="build-new-file-cancel">Cancel</button>
        </div>
        <div className="hint" style={{ marginTop: 6 }}>An empty file in your workspace. To make a model, a connection or an import, use the other buttons.</div>
        {problem && <div className="hint" style={{ marginTop: 6 }} data-testid="build-new-file-problem">{problem}</div>}
        {p.failure && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="build-new-file-failed"><span className="mark">!</span><span>{p.failure}</span></div>}
      </div>
    </div>
  )
}

export function NewFilePanel({ project, workspace, onCreated, onCancel }: { project: string; workspace: string; onCreated: (path: string) => void; onCancel: () => void }) {
  const client = useQueryClient()
  const [path, setPath] = useState('')
  const [failure, setFailure] = useState<string | null>(null)
  const create = useMutation({
    mutationFn: () => buildApi.createFile(project, workspace, path.trim()),
    onSuccess: async (answer) => {
      const problem = fileFailure(answer, 'The file could not be made.')
      setFailure(problem)
      if (problem) return
      await client.invalidateQueries({ queryKey: ['build', project, workspace] })
      onCreated(path.trim())
    },
  })
  return (
    <>
      <NewFileView path={path} busy={create.isPending} failure={failure} onPath={(value) => { setPath(value); setFailure(null) }} onCreate={() => create.mutate()} onCancel={onCancel} />
      <ErrorBanner error={create.error} />
    </>
  )
}

export interface OpenFileToolsViewProps {
  path: string
  to: string
  confirming: boolean
  dirty: boolean
  busy: boolean
  failure: string | null
  onTo: (to: string) => void
  onRename: () => void
  onAskDelete: () => void
  onDelete: () => void
  onKeep: () => void
}

/** Rename and delete for the file that is open. A file with unsaved changes is neither renamed nor deleted from here: save it or leave it first. Delete asks twice. */
export function OpenFileToolsView(p: OpenFileToolsViewProps) {
  const problem = filePathProblem(p.to)
  const canRename = p.to !== '' && p.to !== p.path && problem === null && !p.dirty && !p.busy
  return (
    <div className="row" style={{ gap: 8, margin: '8px 0', flexWrap: 'wrap', alignItems: 'baseline' }} data-testid="build-file-tools">
      <input className="mono" value={p.to} onChange={(e) => p.onTo(e.target.value)} aria-label="New path of the file" data-testid="build-file-rename-to" style={{ minWidth: 260 }} />
      <button type="button" className="btn btn-sm" disabled={!canRename} onClick={p.onRename} data-testid="build-file-rename">Rename</button>
      {!p.confirming && <button type="button" className="btn btn-sm" disabled={p.dirty || p.busy} onClick={p.onAskDelete} data-testid="build-file-delete">Delete</button>}
      {p.confirming && (
        <>
          <span className="hint">Delete {p.path} from your workspace?</span>
          <button type="button" className="btn btn-sm btn-danger" disabled={p.busy} onClick={p.onDelete} data-testid="build-file-delete-confirm">Yes, delete it</button>
          <button type="button" className="btn btn-sm" onClick={p.onKeep} data-testid="build-file-delete-cancel">Keep it</button>
        </>
      )}
      {p.dirty && <span className="hint">Save the changes first.</span>}
      {problem && <span className="hint" data-testid="build-file-rename-problem">{problem}</span>}
      {p.failure && <span className="banner error" role="alert" data-testid="build-file-failed"><span className="mark">!</span><span>{p.failure}</span></span>}
    </div>
  )
}

export function OpenFileTools({ project, workspace, path, hash, dirty, onMoved, onDeleted }: { project: string; workspace: string; path: string; hash: string; dirty: boolean; onMoved: (to: string) => void; onDeleted: () => void }) {
  const client = useQueryClient()
  const [to, setTo] = useState(path)
  const [confirming, setConfirming] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)
  const changed = async () => {
    await client.invalidateQueries({ queryKey: ['build', project, workspace] })
  }
  const rename = useMutation({
    mutationFn: () => buildApi.moveFile(project, workspace, path, to.trim(), hash),
    onSuccess: async (answer) => {
      const problem = fileFailure(answer, 'The file could not be renamed.')
      setFailure(problem)
      if (problem) return
      await changed()
      onMoved(to.trim())
    },
  })
  const remove = useMutation({
    mutationFn: () => buildApi.deleteFile(project, workspace, path, hash),
    onSuccess: async (answer) => {
      const problem = fileFailure(answer, 'The file could not be deleted.')
      setFailure(problem)
      setConfirming(false)
      if (problem) return
      await changed()
      onDeleted()
    },
  })
  return (
    <>
      <OpenFileToolsView
        path={path} to={to} confirming={confirming} dirty={dirty} busy={rename.isPending || remove.isPending} failure={failure}
        onTo={(value) => { setTo(value); setFailure(null) }} onRename={() => rename.mutate()} onAskDelete={() => setConfirming(true)} onDelete={() => remove.mutate()} onKeep={() => setConfirming(false)}
      />
      <ErrorBanner error={rename.error ?? remove.error} />
    </>
  )
}
