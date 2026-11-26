import { useEffect, useRef, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { DraftProvider, findingsKey, filesKey, useDraft, useDraftFile } from '../draft'
import { designerOf, designerPath, filesPath } from '../designers'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, CodeEditor, DiffViewer, ErrorBanner, Link, Navigate, useIsAdmin, useNavigate } from '@dbdatakit/shell'
import { workspaceApi, type Change } from '../api'
import { commitState, conflictsOf, describeEvent, fileTree, languageOf, mergeState, pathProblem, saveState, statusWord, type TreeNode } from '../model'
import { WorkspaceShell } from '../WorkspaceShell'
import { BuildsPanel } from './BuildsPanel'
import { CheckPanel } from './CheckPanel'
import { NewStoreModel, StoreModelEditor } from './StoreModelPanel'
import { BuildPlanPanel } from './BuildPlanPanel'
import { BuildCompilePanel } from './BuildCompilePanel'
import { BuildViewsPanel } from './BuildViewsPanel'
import { FileHistory } from './FileHistory'
import { BuildFileEditor, NewBuildConnection, NewBuildModel } from './BuildFilePanel'
import { CatalogPanel, useCatalogRead } from './CatalogPanel'
import { SyncProvisioningPanel } from './SyncProvisioningPanel'
import { ProjectRunsPanel } from './ProjectRunsPanel'
import { StoreDeployPanel } from './StoreDeployPanel'
import { StoreRowsPanel } from './StoreRowsPanel'
import { SyncScriptsPanel } from './SyncScriptsPanel'
import { SyncFileEditor, SyncSetOverview } from './SyncSetPanel'
import { IntegrationManifestEditor, IntegrationOverview } from './IntegrationPanel'
import { editorKindOf } from '../editors'
import { useLiveCheck } from '../useLiveCheck'

const when = (iso: string) => (iso ? new Date(iso).toLocaleString() : '')

/** The files of the draft as a tree; a click opens one. */
function Tree({ nodes, selected, onOpen, dirty, depth = 0 }: { nodes: TreeNode[]; selected: string | null; onOpen: (path: string) => void; dirty: readonly string[]; depth?: number }) {
  return (
    <>
      {nodes.map((n) => n.folder ? (
        <div key={n.path}>
          <div className="mono muted" style={{ paddingLeft: depth * 14 }} data-testid="wsp-folder" data-path={n.path}>{n.name}/</div>
          <Tree nodes={n.children} selected={selected} onOpen={onOpen} dirty={dirty} depth={depth + 1} />
        </div>
      ) : (
        <button key={n.path} type="button" className={`grid-row mono ${selected === n.path ? 'active' : ''}`} style={{ width: '100%', textAlign: 'left', paddingLeft: depth * 14 + 4 }}
          data-testid="wsp-file" data-path={n.path} data-dirty={dirty.includes(n.path)} aria-current={selected === n.path} onClick={() => onOpen(n.path)}>{n.name}{dirty.includes(n.path) ? <span className="hint"> (not saved)</span> : null}</button>
      ))}
    </>
  )
}

/** One file in the editor: what is on the server, what has been typed (an unsaved edit of the draft, kept by the frame for both areas), and Save, Move and Delete for the owner. */
function FileEditor({ project, id, kind, path, paths, editable, canWrite, onGone, onRenamed, onOpen }: { project: string; id: string; kind?: string; path: string; paths: readonly string[]; editable: boolean; canWrite: boolean; onGone: () => void; onRenamed: (to: string) => void; onOpen: (path: string) => void }) {
  const client = useQueryClient()
  const draft = useDraft()
  const file = useDraftFile(path)
  const [target, setTarget] = useState('')
  const [moving, setMoving] = useState(false)
  const [asking, setAsking] = useState(false)
  const [showHistory, setShowHistory] = useState(false)
  // what was typed in this editor since it opened: only that is saved by the live check, so an edit made in the other area stays not saved until Save
  const [typedHere, setTypedHere] = useState(false)
  // set once the server has moved or deleted the file: the path has left the draft, and this editor stays on screen until the address changes. When the
  // file list comes back without it the editor's text turns empty, and Monaco reports a text set from outside as a change, so without this the frame
  // would take that empty text as a new unsaved file at the old path and the tree would show it again.
  const closed = useRef(false)
  const loaded = { data: file.saved, isLoading: file.loading, error: file.error }
  const text = file.text ?? ''
  const dirty = file.dirty
  const refresh = async () => { await client.invalidateQueries({ queryKey: findingsKey(project, id) }); await client.invalidateQueries({ queryKey: ['workspace', 'changes', project, id] }) }
  const save = useMutation({ mutationFn: () => draft.saveOne(path) })
  const move = useMutation({
    mutationFn: () => workspaceApi.moveFile(project, id, path, target.trim()),
    onSuccess: async (r) => { closed.current = true; setMoving(false); draft.rename(path, r.to); await client.invalidateQueries({ queryKey: filesKey(project, id) }); await refresh(); onRenamed(r.to) },
  })
  const remove = useMutation({
    mutationFn: async () => { if (!draft.isNew(path)) await workspaceApi.deleteFile(project, id, path) },
    onSuccess: async () => { closed.current = true; setAsking(false); draft.discard(path); await client.invalidateQueries({ queryKey: filesKey(project, id) }); await refresh(); onGone() },
  })
  const state = saveState({ mine: editable, canWrite, dirty, busy: save.isPending })
  const problem = pathProblem(target.trim())
  const editorKind = editorKindOf(kind, path)
  const catalog = useCatalogRead(project, id)
  const live = useLiveCheck({ project, id, text, enabled: editorKind !== null && editable && canWrite && !loaded.isLoading && !loaded.error, flush: async () => (typedHere && dirty ? await save.mutateAsync() : file.saved) })
  const onText = (value: string) => { if (closed.current) return; setTypedHere(true); draft.edit(path, value) }
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
        <button type="button" className="btn" data-testid="wsp-file-history-open" aria-pressed={showHistory} onClick={() => setShowHistory((v) => !v)}>Earlier texts</button>
        {state.why && <span className="hint" data-testid="wsp-save-why">{state.why}</span>}
      </div>
      {showHistory && <FileHistory project={project} id={id} path={path} current={text} canRestore={editable && canWrite} onRestore={(earlier) => { onText(earlier); setShowHistory(false) }} />}
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
      {!loaded.error && !loaded.isLoading && editorKind === 'build-file' && (
        <BuildFileEditor path={path} text={text} onText={onText} readOnly={!editable || !canWrite} live={live} onOpen={onOpen} />
      )}
      {!loaded.error && !loaded.isLoading && editorKind === 'integration-manifest' && (
        <IntegrationManifestEditor path={path} text={text} onText={onText} readOnly={!editable || !canWrite} live={live} onOpen={onOpen} />
      )}
      {!loaded.error && !loaded.isLoading && editorKind !== null && editorKind !== 'store-model' && editorKind !== 'build-file' && editorKind !== 'integration-manifest' && (
        <SyncFileEditor kind={editorKind} path={path} text={text} onText={onText} readOnly={!editable || !canWrite} live={live} onOpen={onOpen} catalog={catalog.read} paths={paths} />
      )}
      {!loaded.error && !loaded.isLoading && editorKind === null && (
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

const TABS = ['changes', 'history', 'builds', 'check', 'models', 'compile', 'views', 'catalog', 'scripts', 'provisioning', 'deploy', 'rows', 'runs'] as const
type Tab = typeof TABS[number]
const tabOf = (value: string | null): Tab => (TABS as readonly string[]).includes(value ?? '') ? value as Tab : 'changes'

/** The parts of the frame both areas draw: the draft's place, the person's rights, and how to open a file or a tab of Files. */
interface AreaProps { project: string; id: string; kind: string | undefined; editable: boolean; canWrite: boolean; openFile: (path: string | null) => void; openTab: (tab: Tab) => void }

/** Writes files the forms of the Files area make (a new model, a new replication) straight into the draft, then opens the first. */
function useMakeFiles(project: string, id: string, openFile: (path: string) => void) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: async (made: { path: string; text: string }[]) => { for (const f of made) await workspaceApi.writeFile(project, id, f.path, f.text); return made[0].path },
    onSuccess: async (first) => {
      await client.invalidateQueries({ queryKey: filesKey(project, id) })
      await client.invalidateQueries({ queryKey: ['workspace', 'changes', project, id] })
      await client.invalidateQueries({ queryKey: findingsKey(project, id) })
      openFile(first)
    },
  })
}

/** The Files area's entries in the navigation: the file tree (with what is not saved marked) and the forms that make a file. */
function FilesNav({ project, id, kind, editable, canWrite, selected, openFile }: AreaProps & { selected: string | null }) {
  const draft = useDraft()
  const [newPath, setNewPath] = useState('')
  const make = useMakeFiles(project, id, openFile)
  const create = useMakeFiles(project, id, (path) => { setNewPath(''); openFile(path) })
  const problem = pathProblem(newPath.trim())
  const tree = draft.paths.map((path) => ({ path, size: 0 }))
  return (
    <div className="card" data-testid="wsp-files">
      <div className="card-body">
        <Link to={filesPath(project, id)} className="hint" style={{ display: 'block', marginBottom: 6 }} data-testid="wsp-nav-files">Files</Link>
        <ErrorBanner error={draft.filesError} />
        <Tree nodes={fileTree(tree)} selected={selected} onOpen={openFile} dirty={draft.dirtyPaths} />
        {draft.truncated && <div className="hint">Only the first files are shown.</div>}
        {editable && canWrite && (
          <form className="row" style={{ gap: 6, marginTop: 10, flexWrap: 'wrap' }} onSubmit={(e) => { e.preventDefault(); if (!problem && newPath.trim() !== '') create.mutate([{ path: newPath.trim(), text: '' }]) }}>
            <input className="input mono" style={{ flex: 1, minWidth: 120 }} aria-label="New file path" placeholder="views/orders.yaml" value={newPath} onChange={(e) => setNewPath(e.target.value)} data-testid="wsp-new-path" />
            <button type="submit" className="btn" disabled={!!problem || newPath.trim() === '' || create.isPending} data-testid="wsp-new-file">New file</button>
            {problem && <span className="hint" data-testid="wsp-new-why">{problem}</span>}
          </form>
        )}
        {kind === 'store' && editable && canWrite && (
          <NewStoreModel taken={draft.paths} busy={make.isPending} onCreate={(path, text) => make.mutate([{ path, text }])} />
        )}
        {kind === 'build' && editable && canWrite && (
          <>
            <NewBuildModel paths={draft.paths} busy={make.isPending} onCreate={(made) => make.mutate(made)} />
            <NewBuildConnection paths={draft.paths} busy={make.isPending} onCreate={(path, text) => make.mutate([{ path, text }])} />
          </>
        )}
        <ErrorBanner error={create.error ?? make.error} />
      </div>
    </div>
  )
}

/** The overviews a kind has in the Files area (a Sync set's replications and connections, an integration's package), above the tree as they always were. */
function FilesOverview({ project, id, kind, editable, canWrite, openFile, openTab }: AreaProps) {
  const draft = useDraft()
  const make = useMakeFiles(project, id, openFile)
  return (
    <>
      {kind === 'sync' && <SyncSetOverview project={project} id={id} paths={draft.paths} canWrite={editable && canWrite} busy={make.isPending} onOpen={openFile} onCreate={(made) => make.mutate(made)} />}
      {kind === 'integration' && <IntegrationOverview project={project} id={id} paths={draft.paths} canWrite={editable && canWrite} busy={make.isPending} onOpen={openFile} onCreate={(made) => make.mutate(made)} onBuildStarted={() => openTab('builds')} />}
      <ErrorBanner error={make.error} />
    </>
  )
}

/** One tab button of the Files area. */
function TabButton({ tab, at, label, openTab }: { tab: Tab; at: Tab; label: string; openTab: (tab: Tab) => void }) {
  return <button type="button" role="tab" aria-selected={tab === at} className={`subtab ${tab === at ? 'active' : ''}`} data-testid={`wsp-tab-${at}`} onClick={() => openTab(at)}>{label}</button>
}

/** The Files area: the file open (from the address) in its editor, and the draft's tabs below it (the tab from the address too), as the page always was. */
function FilesArea({ project, id, kind, editable, canWrite, openFile, openTab, selected, tab, onMerged }: AreaProps & { selected: string | null; tab: Tab; onMerged: (sha: string | null) => void }) {
  const draft = useDraft()
  const paths = draft.paths
  const make = useMakeFiles(project, id, openFile)
  const t = (at: Tab, label: string) => <TabButton tab={tab} at={at} label={label} openTab={openTab} />
  return (
    <div data-testid="wsp-area-files">
      {selected === null
        ? <div className="empty" data-testid="wsp-pick">Pick a file to read{editable ? ' or edit' : ''} it.</div>
        : <FileEditor key={selected} project={project} id={id} kind={kind} path={selected} paths={paths} editable={editable && !(kind === 'build' && selected.startsWith('rendered/'))} canWrite={canWrite} onGone={() => openFile(null)} onRenamed={openFile} onOpen={openFile} />}
      <div className="subtabbar" style={{ margin: '18px 0 8px' }} role="tablist">
        {t('changes', 'Changes')}
        {t('check', 'Check')}
        {kind === 'build' && t('models', 'Models and plan')}
        {kind === 'build' && t('compile', 'Compile')}
        {kind === 'build' && t('views', 'Views')}
        {kind === 'sync' && t('scripts', 'Scripts')}
        {kind === 'sync' && t('provisioning', 'Provisioning')}
        {kind === 'store' && t('rows', 'Rows')}
        {kind === 'store' && t('deploy', 'Deploy')}
        {(kind === 'build' || kind === 'sync') && t('catalog', 'Catalog')}
        {(kind === 'build' || kind === 'sync') && t('runs', 'Runs')}
        {t('builds', 'Builds')}
        {t('history', 'History')}
      </div>
      {tab === 'changes'
        ? <ChangesPanel project={project} id={id} editable={editable} canWrite={canWrite} onMerged={onMerged} />
        : tab === 'models' && kind === 'build'
          ? <BuildPlanPanel project={project} id={id} paths={paths} onOpen={openFile} />
        : tab === 'compile' && kind === 'build'
          ? <BuildCompilePanel project={project} id={id} paths={paths} canWrite={editable && canWrite} />
        : tab === 'views' && kind === 'build'
          ? <BuildViewsPanel project={project} id={id} paths={paths} canWrite={editable && canWrite} />
        : tab === 'scripts' && kind === 'sync'
          ? <SyncScriptsPanel project={project} id={id} paths={paths} canWrite={editable && canWrite} busy={make.isPending} onOpen={openFile} onCreate={(made) => make.mutate(made)} />
        : tab === 'provisioning' && kind === 'sync'
          ? <SyncProvisioningPanel project={project} id={id} paths={paths} />
        : tab === 'runs' && (kind === 'build' || kind === 'sync')
          ? <ProjectRunsPanel project={project} id={id} kind={kind} paths={paths} />
        : tab === 'rows' && kind === 'store'
          ? <StoreRowsPanel paths={paths} canWrite />
        : tab === 'deploy' && kind === 'store'
          ? <StoreDeployPanel project={project} id={id} paths={paths} />
        : tab === 'catalog' && (kind === 'build' || kind === 'sync')
          ? <CatalogPanel project={project} id={id} onOpen={openFile} canWrite={editable && canWrite} kind={kind} />
        : tab === 'check'
          ? <CheckPanel project={project} id={id} onOpen={openFile} />
          : tab === 'builds'
            ? <BuildsPanel project={project} id={id} canWrite={canWrite} />
            : <HistoryPanel project={project} id={id} />}
    </div>
  )
}

/** The draft's chrome, the same in both areas: what is not saved with Save, the check's count opening Check, and Commit… opening Changes. */
function DraftChrome({ project, id, editable, canWrite, openTab }: { project: string; id: string; editable: boolean; canWrite: boolean; openTab: (tab: Tab) => void }) {
  const draft = useDraft()
  const check = useQuery({ queryKey: findingsKey(project, id), queryFn: () => workspaceApi.check(project, id), staleTime: 30_000, retry: false })
  const errors = (check.data?.diagnostics ?? []).filter((d) => d.severity === 'error').length
  const unsaved = draft.dirtyPaths.length
  return (
    <div className="row" style={{ gap: 10, alignItems: 'center', flexWrap: 'wrap', margin: '0 0 10px' }} data-testid="wsp-chrome">
      {unsaved > 0 && <span className="hint" data-testid="wsp-unsaved" data-count={unsaved} title={draft.dirtyPaths.join(', ')}>{unsaved === 1 ? '1 file not saved' : `${unsaved} files not saved`}</span>}
      {editable && canWrite && <button type="button" className="btn btn-primary" disabled={unsaved === 0 || draft.saving} data-testid="wsp-save-all" onClick={() => void draft.saveAll()}>{draft.saving ? 'Saving...' : 'Save'}</button>}
      <button type="button" className="btn-link" data-testid="wsp-check-count" data-errors={check.data ? errors : ''} onClick={() => openTab('check')}>
        {check.isLoading ? 'Checking...' : check.error ? 'Check' : errors > 0 ? `${errors} to fix` : 'No problems found'}
      </button>
      <button type="button" className="btn" data-testid="wsp-commit-open" disabled={!editable || !canWrite} onClick={() => openTab('changes')}>Commit…</button>
      <ErrorBanner error={draft.saveError} />
    </div>
  )
}

/** Where an address after `/:project/:draft/` is: the area, the file open in Files, the place in the Designer. */
export function placeOf(splat: string): { area: 'files' | 'designer' | null; file: string | null; rest: string } {
  if (splat === 'designer' || splat.startsWith('designer/')) return { area: 'designer', file: null, rest: splat.slice('designer/'.length) }
  if (splat === 'files' || splat.startsWith('files/')) return { area: 'files', file: splat.slice('files/'.length) || null, rest: '' }
  return { area: null, file: null, rest: '' }
}

/**
 * One draft of a project, in its two areas (`DbDataWorkspace/README.md`, "A project's two areas"): Files at `/:project/:draft/files/<path>` and,
 * for a kind that registers one, the Designer at `/:project/:draft/designer/...`. The address alone says which is open; a draft of a kind with a
 * Designer lands on it, any other on Files. The frame holds the draft's unsaved edits, so both areas see the same ones, and the chrome above both.
 */
export function WorkspacePage() {
  const { project = '', id = '', '*': splat = '' } = useParams()
  const navigate = useNavigate()
  const client = useQueryClient()
  const canWrite = useIsAdmin()
  const [search] = useSearchParams()
  const [asking, setAsking] = useState(false)
  const [merged, setMerged] = useState<{ sha: string | null } | null>(null)
  const workspaces = useQuery({ queryKey: ['workspace', 'list', project], queryFn: () => workspaceApi.workspaces(project) })
  const mine = workspaces.data?.find((w) => w.id === id)
  const projects = useQuery({ queryKey: ['workspace', 'projects'], queryFn: () => workspaceApi.projects() })
  const kind = projects.data?.find((p) => p.id === project)?.kind
  const designer = designerOf(kind)
  const abandon = useMutation({
    mutationFn: () => workspaceApi.abandon(project, id),
    onSuccess: async () => { await client.invalidateQueries({ queryKey: ['workspace'] }); navigate('/') },
  })
  const editable = mine?.mine ?? false
  const crumbs = [{ label: 'Workspaces', to: '/' }, { label: project, mono: true }, { label: mine?.name ?? id, mono: true, heading: true }]
  const { area, file: selected, rest } = placeOf(splat)
  const tab = tabOf(search.get('tab'))
  // where the page is now, read when a file or a tab is opened, not when the call that opens it was made: a Delete or a Move ends after its
  // refreshes, and a tab picked meanwhile stays picked instead of the page going back to the tab of the moment the button was pressed
  const here = useRef({ area, selected, tab, tabNamed: search.get('tab') !== null })
  useEffect(() => { here.current = { area, selected, tab, tabNamed: search.get('tab') !== null } })
  const openFile = (path: string | null) => { const now = here.current; navigate(filesPath(project, id, path, now.area === 'files' && now.tabNamed ? now.tab : undefined)) }
  const openTab = (t: Tab) => { const now = here.current; navigate(filesPath(project, id, now.area === 'files' ? now.selected : null, t), { replace: now.area === 'files' }) }

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
  // the landing page is fixed by the kind; an address naming a tab (`?tab=runs`, where the Runner's screens link back) is a place in Files
  if (area === null && projects.data) {
    const tabbed = search.get('tab')
    return <Navigate to={designer && !tabbed ? designerPath(project, id) : filesPath(project, id, null, tabbed ?? undefined)} replace />
  }
  if (area === 'designer' && projects.data && !designer) return <Navigate to={filesPath(project, id)} replace />
  const props: AreaProps = { project, id, kind, editable, canWrite, openFile, openTab }
  return (
    <WorkspaceShell crumbs={crumbs}>
      <div className="pane" data-testid="wsp-page" data-workspace={id} data-area={area ?? ''}>
        <ErrorBanner error={workspaces.error} />
        {workspaces.data && !mine && <div className="empty" data-testid="wsp-missing">There is no workspace {id} in {project}. <Link to="/">Back to the workspaces</Link></div>}
        {mine && (
          <DraftProvider project={project} id={id} kind={kind} canEdit={editable && canWrite}>
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
            <DraftChrome project={project} id={id} editable={editable} canWrite={canWrite} openTab={openTab} />
            <div className="row" style={{ gap: 16, alignItems: 'flex-start', flexWrap: 'wrap' }}>
              <nav style={{ flex: '0 0 300px', minWidth: 240, display: 'grid', gap: 12 }} aria-label="The draft" data-testid="wsp-nav">
                {designer && (
                  <div className="card" data-testid="wsp-nav-designer" data-kind={designer.kind} aria-current={area === 'designer'}>
                    <div className="card-body">
                      <Link to={designerPath(project, id)} className="hint" style={{ display: 'block', marginBottom: 6 }} data-testid="wsp-nav-designer-open">{designer.title}</Link>
                      <designer.Navigation project={project} id={id} rest={rest} />
                    </div>
                  </div>
                )}
                {area === 'files' && <FilesOverview {...props} />}
                <FilesNav {...props} selected={selected} />
              </nav>
              <div style={{ flex: 1, minWidth: 320 }}>
                {area === 'files' && <FilesArea {...props} selected={selected} tab={tab} onMerged={(sha) => { setMerged({ sha }); void client.invalidateQueries({ queryKey: ['workspace', 'list', project] }) }} />}
                {area === 'designer' && designer && <div data-testid="wsp-area-designer" data-kind={designer.kind}><designer.Page project={project} id={id} rest={rest} /></div>}
              </div>
            </div>
          </DraftProvider>
        )}
      </div>
    </WorkspaceShell>
  )
}
