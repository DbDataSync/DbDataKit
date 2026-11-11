import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, CodeEditor, DiffViewer, ErrorBanner, Link, useIsAdmin, useNavigate } from '@dbdatakit/shell'
import { workspaceApi, type Change } from '../api'
import { commitState, conflictsOf, describeEvent, fileTree, languageOf, mergeState, pathProblem, saveState, statusWord, type TreeNode } from '../model'
import { WorkspaceShell } from '../WorkspaceShell'
import { BuildsPanel } from './BuildsPanel'
import { CheckPanel } from './CheckPanel'
import { NewStoreModel, StoreModelEditor } from './StoreModelPanel'
import { editorKindOf } from '../editors'
import { useLiveCheck } from '../useLiveCheck'

const when = (iso: string) => (iso ? new Date(iso).toLocaleString() : '')

/** The files of the draft as a tree; a click opens one. */
function Tree({ nodes, selected, onOpen, depth = 0 }: { nodes: TreeNode[]; selected: string | null; onOpen: (path: string) => void; depth?: number }) {
  return (
    <>
      {nodes.map((n) => n.folder ? (
        <div key={n.path}>
          <div className="mono muted" style={{ paddingLeft: depth * 14 }} data-testid="wsp-folder" data-path={n.path}>{n.name}/</div>
          <Tree nodes={n.children} selected={selected} onOpen={onOpen} depth={depth + 1} />
        </div>
      ) : (
        <button key={n.path} type="button" className={`grid-row mono ${selected === n.path ? 'active' : ''}`} style={{ width: '100%', textAlign: 'left', paddingLeft: depth * 14 + 4 }}
          data-testid="wsp-file" data-path={n.path} aria-current={selected === n.path} onClick={() => onOpen(n.path)}>{n.name}</button>
      ))}
    </>
  )
}

/** One file in the editor: what is on the server, what has been typed, and Save, Move and Delete for the owner. */
function FileEditor({ project, id, kind, path, editable, canWrite, onGone, onRenamed, onOpen }: { project: string; id: string; kind?: string; path: string; editable: boolean; canWrite: boolean; onGone: () => void; onRenamed: (to: string) => void; onOpen: (path: string) => void }) {
  const client = useQueryClient()
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [target, setTarget] = useState('')
  const [moving, setMoving] = useState(false)
  const [asking, setAsking] = useState(false)
  const loaded = useQuery({ queryKey: ['workspace', 'file', project, id, path], queryFn: () => workspaceApi.readFile(project, id, path), staleTime: Infinity, gcTime: 0, retry: false })
  const text = drafts[path] ?? loaded.data ?? ''
  const dirty = drafts[path] !== undefined && drafts[path] !== loaded.data
  const refresh = () => client.invalidateQueries({ queryKey: ['workspace', 'changes', project, id] })
  const save = useMutation({
    mutationFn: async () => { const sent = text; await workspaceApi.writeFile(project, id, path, sent); return sent },
    onSuccess: async (sent) => {
      // what the server holds is what was sent: set it before the draft goes, or the editor shows the old text for a moment and its change event makes that the draft again
      client.setQueryData(['workspace', 'file', project, id, path], sent)
      // a person who typed on while the save ran keeps what they typed (the live check saves as they type)
      setDrafts((d) => (d[path] === sent ? (({ [path]: _gone, ...rest }) => rest)(d) : d))
      await client.invalidateQueries({ queryKey: ['workspace', 'files', project, id] })
      await refresh()
    },
  })
  const move = useMutation({
    mutationFn: () => workspaceApi.moveFile(project, id, path, target.trim()),
    onSuccess: async (r) => { setMoving(false); await client.invalidateQueries({ queryKey: ['workspace', 'files', project, id] }); await refresh(); onRenamed(r.to) },
  })
  const remove = useMutation({
    mutationFn: () => workspaceApi.deleteFile(project, id, path),
    onSuccess: async () => { setAsking(false); await client.invalidateQueries({ queryKey: ['workspace', 'files', project, id] }); await refresh(); onGone() },
  })
  const state = saveState({ mine: editable, canWrite, dirty, busy: save.isPending })
  const problem = pathProblem(target.trim())
  const editorKind = editorKindOf(kind, path)
  const live = useLiveCheck({ project, id, text, enabled: editorKind === 'store-model' && editable && canWrite && !loaded.isLoading && !loaded.error, flush: async () => { if (dirty) await save.mutateAsync() } })
  const onText = (value: string) => setDrafts((d) => ({ ...d, [path]: value }))
  return (
    <div data-testid="wsp-editor" data-path={path}>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginBottom: 8 }}>
        <span className="mono" data-testid="wsp-editor-path">{path}</span>
        {dirty && <span className="hint" data-testid="wsp-dirty">not saved</span>}
        <button type="button" className="btn btn-primary" disabled={!state.enabled} title={state.why ?? undefined} data-testid="wsp-save" onClick={() => save.mutate()}>Save</button>
        {editable && canWrite && !moving && <button type="button" className="btn" data-testid="wsp-move-open" onClick={() => { setTarget(path); setMoving(true) }}>Move</button>}
        {editable && canWrite && !asking && <button type="button" className="btn" data-testid="wsp-delete" onClick={() => setAsking(true)}>Delete</button>}
        {asking && (
          <>
            <span className="hint">The file is removed from this workspace (it comes back only from a commit that has it).</span>
            <button type="button" className="btn btn-danger" disabled={remove.isPending} data-testid="wsp-delete-confirm" onClick={() => remove.mutate()}>Delete it</button>
            <button type="button" className="btn" data-testid="wsp-delete-cancel" onClick={() => setAsking(false)}>Keep it</button>
          </>
        )}
        {state.why && <span className="hint" data-testid="wsp-save-why">{state.why}</span>}
      </div>
      {moving && (
        <form className="row" style={{ gap: 8, alignItems: 'center', marginBottom: 8, flexWrap: 'wrap' }} onSubmit={(e) => { e.preventDefault(); if (!problem && target.trim() !== '' && target.trim() !== path) move.mutate() }}>
          <input className="input mono" aria-label="New path" value={target} onChange={(e) => setTarget(e.target.value)} data-testid="wsp-move-target" />
          <button type="submit" className="btn btn-primary" disabled={!!problem || target.trim() === '' || target.trim() === path || move.isPending} data-testid="wsp-move-go">Move it</button>
          <button type="button" className="btn" data-testid="wsp-move-cancel" onClick={() => setMoving(false)}>Cancel</button>
          {problem && <span className="hint" data-testid="wsp-move-why">{problem}</span>}
        </form>
      )}
      <ErrorBanner error={loaded.error ?? save.error ?? move.error ?? remove.error} />
      {loaded.isLoading && <div className="empty">Loading...</div>}
      {!loaded.error && !loaded.isLoading && editorKind === 'store-model' && (
        <StoreModelEditor path={path} text={text} onText={onText} readOnly={!editable || !canWrite} live={live} onOpen={onOpen} />
      )}
      {!loaded.error && !loaded.isLoading && editorKind !== 'store-model' && (
        <CodeEditor value={text} language={languageOf(path)} readOnly={!editable || !canWrite} minLines={14} maxLines={30} testId="wsp-code"
          onChange={onText} />
      )}
    </div>
  )
}

/** A changed file: its status, and both texts side by side when the server sent them, the patch when it did not. */
function ChangeRow({ change }: { change: Change }) {
  const [open, setOpen] = useState(false)
  return (
    <div data-testid="wsp-change" data-path={change.path} data-status={statusWord(change.status)}>
      <button type="button" className="grid-row mono" style={{ width: '100%', textAlign: 'left' }} onClick={() => setOpen((v) => !v)} data-testid="wsp-change-toggle">{statusWord(change.status)} {change.path}</button>
      {open && (change.sides
        ? <div data-testid="wsp-diff"><DiffViewer before={change.before} after={change.after} language={languageOf(change.path)} height={260} testId="wsp-diff-editor" /></div>
        : <pre className="mono" style={{ margin: 0, overflow: 'auto', maxHeight: 220 }} data-testid="wsp-patch">{change.patch}</pre>)}
    </div>
  )
}

/** What changed, commit it, push the branch or merge it; each refusal is the server's sentence. */
function ChangesPanel({ project, id, editable, canWrite, onMerged }: { project: string; id: string; editable: boolean; canWrite: boolean; onMerged: (sha: string | null) => void }) {
  const client = useQueryClient()
  const [message, setMessage] = useState('')
  const changes = useQuery({ queryKey: ['workspace', 'changes', project, id], queryFn: () => workspaceApi.changes(project, id) })
  const history = useQuery({ queryKey: ['workspace', 'history', project, id], queryFn: () => workspaceApi.history(project, id) })
  const refresh = async () => { await client.invalidateQueries({ queryKey: ['workspace', 'changes', project, id] }); await client.invalidateQueries({ queryKey: ['workspace', 'history', project, id] }); await client.invalidateQueries({ queryKey: ['workspace', 'events', project, id] }) }
  const commit = useMutation({ mutationFn: () => workspaceApi.commit(project, id, message.trim()), onSuccess: async () => { setMessage(''); await refresh() } })
  const push = useMutation({ mutationFn: () => workspaceApi.push(project, id), onSuccess: refresh })
  const merge = useMutation({ mutationFn: () => workspaceApi.merge(project, id), onSuccess: (r) => onMerged(r.sha) })
  const list = changes.data?.changes ?? []
  const uncommitted = changes.data?.uncommitted ?? false
  const commits = history.data ?? []
  const c = commitState({ mine: editable, canWrite, message, uncommitted, busy: commit.isPending })
  const m = mergeState({ commits: commits.length, uncommitted, busy: merge.isPending })
  const conflicts = merge.error instanceof ApiError ? conflictsOf(merge.error.body) : []
  return (
    <div data-testid="wsp-changes">
      <ErrorBanner error={changes.error ?? history.error} />
      {list.length === 0 && !changes.isLoading && <div className="empty" data-testid="wsp-changes-none">Nothing differs from where the workspace started.</div>}
      {list.map((ch) => <ChangeRow key={ch.path} change={ch} />)}
      <form className="row" style={{ gap: 10, margin: '12px 0 6px', flexWrap: 'wrap', alignItems: 'center' }} onSubmit={(e) => { e.preventDefault(); if (c.enabled) commit.mutate() }}>
        <input className="input" style={{ flex: 1, minWidth: 220 }} placeholder="what this change is" aria-label="Commit message" value={message} onChange={(e) => setMessage(e.target.value)} data-testid="wsp-commit-message" />
        <button type="submit" className="btn" disabled={!c.enabled} title={c.why ?? undefined} data-testid="wsp-commit">Commit</button>
        <button type="button" className="btn" disabled={!editable || !canWrite || commits.length === 0 || push.isPending} data-testid="wsp-push" onClick={() => push.mutate()}>Push the branch</button>
        <button type="button" className="btn btn-primary" disabled={!m.enabled} title={m.why ?? undefined} data-testid="wsp-merge" onClick={() => merge.mutate()}>Merge into the project</button>
      </form>
      {c.why && uncommitted === false && commits.length > 0 && <div className="hint" data-testid="wsp-commit-why">{c.why}</div>}
      {m.why && <div className="hint" data-testid="wsp-merge-why">{m.why}</div>}
      <div className="hint">Merging needs the approver role and is the project's: what a merge takes is what the scheduler and the deploys read. A project that follows a git remote is pushed to and merged where the remote lives.</div>
      {commit.isSuccess && <div className="hint" data-testid="wsp-committed">{commit.data.committed ? `Committed ${commit.data.sha?.slice(0, 8) ?? ''}.` : 'There was nothing to commit.'}</div>}
      {push.isSuccess && (
        <div className="hint" data-testid="wsp-pushed">Pushed {push.data.branch}. {push.data.pull_request_url
          ? <a href={push.data.pull_request_url} target="_blank" rel="noopener noreferrer" data-testid="wsp-pull-request">Open a pull request</a>
          : 'Open the pull request where the remote lives.'}</div>
      )}
      {conflicts.length > 0 && <div className="empty" data-testid="wsp-conflicts">Nothing was merged. These files conflict with the project: {conflicts.join(', ')}</div>}
      <ErrorBanner error={commit.error ?? push.error ?? merge.error} />
    </div>
  )
}

/** The commits of the draft, and what the server recorded as happening to it. */
function HistoryPanel({ project, id }: { project: string; id: string }) {
  const history = useQuery({ queryKey: ['workspace', 'history', project, id], queryFn: () => workspaceApi.history(project, id) })
  const events = useQuery({ queryKey: ['workspace', 'events', project, id], queryFn: () => workspaceApi.events(project, id) })
  return (
    <div data-testid="wsp-history">
      <ErrorBanner error={history.error ?? events.error} />
      <div className="hint" style={{ margin: '4px 0' }}>Commits</div>
      {history.data?.length === 0 && <div className="empty">No commits in this workspace yet.</div>}
      {(history.data ?? []).map((c) => <div key={c.sha} className="grid-row" style={{ gap: 14 }} data-testid="wsp-commit-row"><span className="mono">{c.sha.slice(0, 8)}</span><span>{c.message}</span><span className="hint">{c.author}, {when(c.when)}</span></div>)}
      <div className="hint" style={{ margin: '12px 0 4px' }}>What happened</div>
      {(events.data ?? []).map((e, i) => <div key={i} data-testid="wsp-event" data-kind={e.kind}>{describeEvent(e)} <span className="muted">({when(e.at)})</span></div>)}
    </div>
  )
}

/** One workspace: its files and an editor, what changed, its history; commit, push, merge and abandon. */
export function WorkspacePage() {
  const { project = '', id = '' } = useParams()
  const navigate = useNavigate()
  const client = useQueryClient()
  const canWrite = useIsAdmin()
  const [selected, setSelected] = useState<string | null>(null)
  const [tab, setTab] = useState<'changes' | 'history' | 'builds' | 'check'>('changes')
  const [newPath, setNewPath] = useState('')
  const [asking, setAsking] = useState(false)
  const [merged, setMerged] = useState<{ sha: string | null } | null>(null)
  const workspaces = useQuery({ queryKey: ['workspace', 'list', project], queryFn: () => workspaceApi.workspaces(project) })
  const files = useQuery({ queryKey: ['workspace', 'files', project, id], queryFn: () => workspaceApi.files(project, id), enabled: merged === null })
  const mine = workspaces.data?.find((w) => w.id === id)
  const projects = useQuery({ queryKey: ['workspace', 'projects'], queryFn: () => workspaceApi.projects() })
  const kind = projects.data?.find((p) => p.id === project)?.kind
  const create = useMutation({
    mutationFn: () => workspaceApi.writeFile(project, id, newPath.trim(), ''),
    onSuccess: async (r) => {
      setNewPath(''); setSelected(r.path)
      await client.invalidateQueries({ queryKey: ['workspace', 'files', project, id] })
      await client.invalidateQueries({ queryKey: ['workspace', 'changes', project, id] })
    },
  })
  const abandon = useMutation({
    mutationFn: () => workspaceApi.abandon(project, id),
    onSuccess: async () => { await client.invalidateQueries({ queryKey: ['workspace'] }); navigate('/') },
  })
  const editable = mine?.mine ?? false
  const problem = pathProblem(newPath.trim())
  const crumbs = [{ label: 'Workspaces', to: '/' }, { label: project, mono: true }, { label: mine?.name ?? id, mono: true, heading: true }]

  if (merged) {
    return (
      <WorkspaceShell crumbs={crumbs}>
        <div className="pane" data-testid="wsp-merged">
          <div className="empty">Merged into {project}{merged.sha ? ` as ${merged.sha.slice(0, 8)}` : ''}. The workspace is finished and its folder is gone. <Link to="/" data-testid="wsp-back">Back to the workspaces</Link></div>
          <div className="hint" style={{ margin: '12px 0 4px' }}>Its builds</div>
          <BuildsPanel project={project} id={id} canWrite={canWrite} closed />
        </div>
      </WorkspaceShell>
    )
  }
  return (
    <WorkspaceShell crumbs={crumbs}>
      <div className="pane" data-testid="wsp-page" data-workspace={id}>
        <ErrorBanner error={workspaces.error} />
        {workspaces.data && !mine && <div className="empty" data-testid="wsp-missing">There is no workspace {id} in {project}. <Link to="/">Back to the workspaces</Link></div>}
        {mine && (
          <>
            <div className="page-head">
              <h1 className="page-title mono" data-testid="wsp-title">{mine.name}</h1>
              <div className="row" style={{ gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
                <span className="hint" data-testid="wsp-owner">{mine.owner}{mine.mine ? ' (you)' : ''}, branch <span className="mono">{mine.branch}</span></span>
                {!asking && <button type="button" className="btn" disabled={!mine.mine || !canWrite} title={!mine.mine ? 'A workspace is abandoned by its owner.' : undefined} data-testid="wsp-abandon" onClick={() => setAsking(true)}>Abandon</button>}
                {asking && (
                  <>
                    <span className="hint">Whatever was committed here and not merged is lost.</span>
                    <button type="button" className="btn btn-danger" disabled={abandon.isPending} data-testid="wsp-abandon-confirm" onClick={() => abandon.mutate()}>Abandon it</button>
                    <button type="button" className="btn" data-testid="wsp-abandon-cancel" onClick={() => setAsking(false)}>Keep it</button>
                  </>
                )}
              </div>
            </div>
            <ErrorBanner error={abandon.error} />
            {!editable && <div className="hint" data-testid="wsp-readonly">This is {mine.owner}'s workspace: you can read it, and only they edit and commit it.</div>}
            <div className="row" style={{ gap: 16, alignItems: 'flex-start', flexWrap: 'wrap' }}>
              <div className="card" style={{ flex: '0 0 260px', minWidth: 220 }} data-testid="wsp-files">
                <div className="card-body">
                  <div className="hint" style={{ marginBottom: 6 }}>Files</div>
                  <ErrorBanner error={files.error} />
                  <Tree nodes={fileTree(files.data?.files ?? [])} selected={selected} onOpen={setSelected} />
                  {files.data?.truncated && <div className="hint">Only the first files are shown.</div>}
                  {editable && canWrite && (
                    <form className="row" style={{ gap: 6, marginTop: 10, flexWrap: 'wrap' }} onSubmit={(e) => { e.preventDefault(); if (!problem && newPath.trim() !== '') create.mutate() }}>
                      <input className="input mono" style={{ flex: 1, minWidth: 120 }} aria-label="New file path" placeholder="views/orders.yaml" value={newPath} onChange={(e) => setNewPath(e.target.value)} data-testid="wsp-new-path" />
                      <button type="submit" className="btn" disabled={!!problem || newPath.trim() === '' || create.isPending} data-testid="wsp-new-file">New file</button>
                      {problem && <span className="hint" data-testid="wsp-new-why">{problem}</span>}
                    </form>
                  )}
                  {kind === 'store' && editable && canWrite && (
                    <NewStoreModel taken={(files.data?.files ?? []).map((f) => f.path)} busy={create.isPending}
                      onCreate={(path, text) => workspaceApi.writeFile(project, id, path, text).then(async (r) => { setSelected(r.path); await client.invalidateQueries({ queryKey: ['workspace', 'files', project, id] }); await client.invalidateQueries({ queryKey: ['workspace', 'changes', project, id] }) })} />
                  )}
                  <ErrorBanner error={create.error} />
                </div>
              </div>
              <div style={{ flex: 1, minWidth: 320 }}>
                {selected === null
                  ? <div className="empty" data-testid="wsp-pick">Pick a file to read{editable ? ' or edit' : ''} it.</div>
                  : <FileEditor key={selected} project={project} id={id} kind={kind} path={selected} editable={editable} canWrite={canWrite} onGone={() => setSelected(null)} onRenamed={setSelected} onOpen={setSelected} />}
                <div className="row" style={{ gap: 8, margin: '18px 0 8px' }} role="tablist">
                  <button type="button" role="tab" aria-selected={tab === 'changes'} className={`btn ${tab === 'changes' ? 'btn-primary' : ''}`} data-testid="wsp-tab-changes" onClick={() => setTab('changes')}>Changes</button>
                  <button type="button" role="tab" aria-selected={tab === 'check'} className={`btn ${tab === 'check' ? 'btn-primary' : ''}`} data-testid="wsp-tab-check" onClick={() => setTab('check')}>Check</button>
                  <button type="button" role="tab" aria-selected={tab === 'builds'} className={`btn ${tab === 'builds' ? 'btn-primary' : ''}`} data-testid="wsp-tab-builds" onClick={() => setTab('builds')}>Builds</button>
                  <button type="button" role="tab" aria-selected={tab === 'history'} className={`btn ${tab === 'history' ? 'btn-primary' : ''}`} data-testid="wsp-tab-history" onClick={() => setTab('history')}>History</button>
                </div>
                {tab === 'changes'
                  ? <ChangesPanel project={project} id={id} editable={editable} canWrite={canWrite} onMerged={(sha) => { setMerged({ sha }); void client.invalidateQueries({ queryKey: ['workspace', 'list', project] }) }} />
                  : tab === 'check'
                    ? <CheckPanel project={project} id={id} onOpen={setSelected} />
                    : tab === 'builds'
                      ? <BuildsPanel project={project} id={id} canWrite={canWrite} />
                      : <HistoryPanel project={project} id={id} />}
              </div>
            </div>
          </>
        )}
      </div>
    </WorkspaceShell>
  )
}
