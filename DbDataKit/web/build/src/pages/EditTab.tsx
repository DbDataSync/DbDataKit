import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useOutletContext, useSearchParams } from 'react-router-dom'
import { CodeEditor, ErrorBanner, SchemaForm, SchemaSet, schemaFor, type EditorDiagnostic } from '@dbdatakit/shell'
import { buildApi } from '../api'
import { CompilePanel } from './CompilePanel'
import { DefinitionPanel } from './DefinitionPanel'
import { GitPanel } from './GitPanel'
import { ImportPanel } from './ImportPanel'
import { NewModelPanel } from './NewModelPanel'
import { PreviewPanel, isModelQuery } from './PreviewPanel'
import type { Diagnostic, ProjectFile, Workspace } from '../types'

const languageOf = (path: string) => (path.endsWith('.sql') ? 'sql' : path.endsWith('.yml') || path.endsWith('.yaml') ? 'yaml' : 'plaintext')

/** The files of a tree by folder, so the list reads as a tree without drawing one. */
export function groupByFolder(files: ProjectFile[]): { folder: string; files: ProjectFile[] }[] {
  const groups = new Map<string, ProjectFile[]>()
  for (const file of files) {
    const slash = file.path.lastIndexOf('/')
    const folder = slash < 0 ? '' : file.path.slice(0, slash)
    groups.set(folder, [...(groups.get(folder) ?? []), file])
  }
  return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([folder, inFolder]) => ({ folder, files: inFolder }))
}

/** What the editor marks: the diagnostics the compiler gave for this file. A diagnostic with no line is anchored to the first. */
export function markersFor(path: string, diagnostics: Diagnostic[]): EditorDiagnostic[] {
  return diagnostics
    .filter((d) => d.location.file === path && d.severity !== 'note')
    .map((d) => ({ line: d.location.line, column: d.location.column, message: `${d.code}: ${d.found}`, severity: d.severity === 'warning' ? 'warning' : 'error' }))
}

/**
 * Editing (plan 13, W04): a draft of the project (a workspace, W02), its files, and the shared editor with what the compiler finds shown as markers. Saving writes the file on the version it was read at and checks the draft again; nothing here
 * changes what the server runs until the draft is merged.
 */
export function EditTab() {
  const { project } = useOutletContext<{ project: string }>()
  const client = useQueryClient()
  const [params, setParams] = useSearchParams()
  const workspace = params.get('workspace')
  const [open, setOpen] = useState<string | null>(null)
  const [text, setText] = useState('')
  const [hash, setHash] = useState('')
  const [saved, setSaved] = useState('')
  const [newName, setNewName] = useState('')
  const [diagnostics, setDiagnostics] = useState<Diagnostic[]>([])
  const [refused, setRefused] = useState<string | null>(null)
  // the panel above the files: a new model, or an import from a connection
  const [panel, setPanel] = useState<'model' | 'import' | null>(null)
  // a file with a schema can be edited as a form or as its YAML; both change the same text
  const [mode, setMode] = useState<'form' | 'yaml'>('form')
  const fileSchemas = useQuery({ queryKey: ['build', 'file-schemas'], queryFn: () => buildApi.fileSchemas(), staleTime: Infinity })
  const schemaSet = useMemo(() => (fileSchemas.data ? new SchemaSet(fileSchemas.data.schemas) : null), [fileSchemas.data])
  const association = open !== null && fileSchemas.data ? schemaFor(fileSchemas.data.associations, open) : undefined

  const projects = useQuery({ queryKey: ['build', 'projects'], queryFn: () => buildApi.projects() })
  const remote = projects.data?.find((p) => p.id === project)?.source === 'git'
  const workspaces = useQuery({ queryKey: ['build', project, 'workspaces'], queryFn: () => buildApi.workspaces(project) })
  const mine = useMemo(() => (workspaces.data ?? []).filter((w) => w.mine), [workspaces.data])
  const files = useQuery({ queryKey: ['build', project, workspace, 'files'], queryFn: () => buildApi.files(project, workspace!), enabled: workspace !== null })

  // only someone the server lets change files is offered the controls that create them (the server checks every call all the same)
  const commands = useQuery({ queryKey: ['build', project, 'commands'], queryFn: () => buildApi.commands(project) })
  const canAuthor = commands.data?.find((c) => c.tool === 'project_model_create')?.allowed === true

  const choose = (id: string | null) => {
    setParams(id ? { workspace: id } : {}, { replace: true })
    setOpen(null)
    setDiagnostics([])
    setPanel(null)
  }
  const make = useMutation({
    mutationFn: () => buildApi.makeWorkspace(project, newName.trim()),
    onSuccess: async (made: Workspace) => {
      setNewName('')
      await client.invalidateQueries({ queryKey: ['build', project, 'workspaces'] })
      choose(made.id)
    },
  })
  const check = useMutation({
    mutationFn: () => buildApi.checkDraft(project, workspace!),
    onSuccess: (answer) => setDiagnostics(answer.document?.diagnostics ?? []),
  })
  const read = useMutation({
    mutationFn: (path: string) => buildApi.readFile(project, workspace!, path),
    onSuccess: (answer) => {
      const file = answer.document?.data
      if (!file) return
      setOpen(file.path)
      setText(file.content)
      setSaved(file.content)
      setHash(file.hash)
      setRefused(null)
    },
  })
  const save = useMutation({
    mutationFn: () => buildApi.writeFile(project, workspace!, open!, text, hash),
    onSuccess: async (answer) => {
      const document = answer.document
      if (!document?.ok) {
        setRefused(document?.errors?.[0] ?? document?.diagnostics?.[0]?.found ?? answer.text ?? 'The server did not write the file.')
        return
      }
      setRefused(null)
      setHash(document.data.hash)
      setSaved(text)
      await client.invalidateQueries({ queryKey: ['build', project, workspace, 'files'] })
      await client.invalidateQueries({ queryKey: ['build', project, workspace, 'git'] })
      check.mutate()
    },
  })

  // a draft is checked when it is opened, so what is already wrong in it shows before anything is changed
  useEffect(() => {
    if (workspace) check.mutate()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspace])

  // what a new model or an import wrote is checked like a save is: the file list and the git panel follow, and the compiler looks at the draft again
  const wrote = async () => {
    await client.invalidateQueries({ queryKey: ['build', project, workspace, 'files'] })
    await client.invalidateQueries({ queryKey: ['build', project, workspace, 'git'] })
    check.mutate()
  }

  const tree = groupByFolder(files.data?.document?.data.files ?? [])
  const problems = diagnostics.filter((d) => d.severity !== 'note' && !d.location.file.startsWith('rendered/'))
  const outOfDateCount = check.data?.document ? (check.data.document.data.out_of_date?.length ?? 0) : null
  const dirty = open !== null && text !== saved

  return (
    <div data-testid="build-edit">
      <div className="row" style={{ gap: 12, marginBottom: 14, flexWrap: 'wrap' }}>
        <select value={workspace ?? ''} onChange={(e) => choose(e.target.value || null)} data-testid="build-edit-workspace" aria-label="Workspace">
          <option value="">Choose a workspace…</option>
          {mine.map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
        </select>
        <input value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="new workspace name" aria-label="New workspace name" data-testid="build-edit-workspace-name" />
        <button type="button" className="btn" disabled={newName.trim() === '' || make.isPending} onClick={() => make.mutate()} data-testid="build-edit-workspace-make">Start a workspace</button>
        <span className="hint">A workspace is your own copy of the project. Nothing you change in it is run until it is merged.</span>
      </div>
      <ErrorBanner error={make.error ?? workspaces.error} />
      {workspace !== null && canAuthor && (
        <div className="row" style={{ gap: 12, marginBottom: 14 }}>
          <button type="button" className={panel === 'model' ? 'btn btn-primary' : 'btn'} aria-pressed={panel === 'model'} onClick={() => setPanel(panel === 'model' ? null : 'model')} data-testid="build-edit-new-model">New model</button>
          <button type="button" className={panel === 'import' ? 'btn btn-primary' : 'btn'} aria-pressed={panel === 'import'} onClick={() => setPanel(panel === 'import' ? null : 'import')} data-testid="build-edit-import">Import from a connection</button>
        </div>
      )}
      {workspace !== null && canAuthor && panel === 'model' && (
        <NewModelPanel
          project={project}
          workspace={workspace}
          onCancel={() => setPanel(null)}
          onCreated={async (query) => { setPanel(null); await wrote(); if (query) read.mutate(query) }}
        />
      )}
      {workspace !== null && canAuthor && panel === 'import' && (
        <ImportPanel project={project} workspace={workspace} onWritten={() => void wrote()} onOpen={(file) => read.mutate(file)} onCancel={() => setPanel(null)} />
      )}
      {workspace !== null && canAuthor && (
        <CompilePanel project={project} workspace={workspace} outOfDate={outOfDateCount} onWritten={() => check.mutate()} />
      )}
      {workspace === null && <div className="empty" data-testid="build-edit-none">Choose or start a workspace to edit the project’s files.</div>}
      {workspace !== null && (
        <div style={{ display: 'grid', gridTemplateColumns: 'minmax(180px, 260px) 1fr', gap: 14, alignItems: 'start' }}>
          <div className="card flush" data-testid="build-edit-files">
            {files.isLoading && <div className="empty">Loading…</div>}
            {tree.map(({ folder, files: inFolder }) => (
              <div key={folder}>
                {folder !== '' && <div className="hint mono" style={{ padding: '6px 12px' }}>{folder}/</div>}
                {inFolder.map((file) => (
                  <button
                    key={file.path}
                    type="button"
                    className="grid-row mono"
                    style={{ width: '100%', textAlign: 'left', fontWeight: file.path === open ? 700 : 400 }}
                    onClick={() => read.mutate(file.path)}
                    data-testid={`build-edit-file-${file.path}`}
                  >
                    {file.path.slice(file.path.lastIndexOf('/') + 1)}
                  </button>
                ))}
              </div>
            ))}
          </div>
          <div>
            {open === null && <div className="empty">Open a file.</div>}
            {open !== null && (
              <>
                <div className="row" style={{ gap: 12, marginBottom: 8 }}>
                  <span className="mono" data-testid="build-edit-open">{open}</span>
                  <button type="button" className="btn btn-primary" disabled={!dirty || save.isPending} onClick={() => save.mutate()} data-testid="build-edit-save">{save.isPending ? 'Saving…' : 'Save'}</button>
                  {!dirty && <span className="hint" data-testid="build-edit-saved">Saved</span>}
                </div>
                {refused && <div className="empty" data-testid="build-edit-refused">{refused}</div>}
                {association && schemaSet && (
                  <div className="row" style={{ gap: 6, marginBottom: 8 }} role="tablist" aria-label="How to edit">
                    <button type="button" role="tab" aria-selected={mode === 'form'} className={mode === 'form' ? 'btn btn-primary btn-sm' : 'btn btn-sm'} onClick={() => setMode('form')} data-testid="build-edit-mode-form">Form</button>
                    <button type="button" role="tab" aria-selected={mode === 'yaml'} className={mode === 'yaml' ? 'btn btn-primary btn-sm' : 'btn btn-sm'} onClick={() => setMode('yaml')} data-testid="build-edit-mode-yaml">YAML</button>
                    <span className="hint">{association.kind}: the form and the YAML are the same file.</span>
                  </div>
                )}
                {association && schemaSet && mode === 'form'
                  ? <SchemaForm schemas={schemaSet} schema={association.schema} text={text} onText={setText} />
                  : <CodeEditor value={text} language={languageOf(open)} onChange={setText} diagnostics={markersFor(open, diagnostics)} minLines={12} maxLines={32} testId="build-edit-editor" />}
              </>
            )}
            {open !== null && workspace !== null && canAuthor && isModelQuery(open) && <DefinitionPanel project={project} workspace={workspace} path={open} dirty={dirty} onWritten={() => void wrote()} />}
            {open !== null && workspace !== null && isModelQuery(open) && <PreviewPanel project={project} workspace={workspace} path={open} version={hash} />}
            <ErrorBanner error={read.error ?? save.error ?? check.error} />
            <div className="card flush" style={{ marginTop: 14 }} data-testid="build-edit-diagnostics">
              <div className="grid-head" style={{ gridTemplateColumns: '0.8fr 2fr 3fr', gap: 14 }}><span>Code</span><span>File</span><span>What</span></div>
              {check.isPending && problems.length === 0 && <div className="empty">Checking…</div>}
              {!check.isPending && problems.length === 0 && <div className="empty" data-testid="build-edit-clean">The compiler found nothing to fix.</div>}
              {problems.map((d, i) => (
                <button key={`${d.code}-${i}`} type="button" className="grid-row" style={{ width: '100%', textAlign: 'left', gridTemplateColumns: '0.8fr 2fr 3fr', gap: 14 }} onClick={() => read.mutate(d.location.file)} data-testid="build-edit-diagnostic">
                  <span className="mono">{d.code}</span>
                  <span className="mono">{d.location.file}</span>
                  <span>{d.found}</span>
                </button>
              ))}
            </div>
          </div>
        </div>
      )}
      {workspace !== null && <GitPanel project={project} workspace={workspace} remote={remote} onMerged={() => choose(null)} />}
    </div>
  )
}
