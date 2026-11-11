import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useOutletContext, useSearchParams } from 'react-router-dom'
import { ErrorBanner, Link, useNavigate } from '@dbdatakit/shell'
import { clientStore, moduleClient, type ViewKind } from '@dbdatakit/visuals'
import { DashboardEditor, type EditorHost } from '@dbdatakit/views-editor'
import { buildApi } from '../api'
import { FOLDERS, buildFilesApi, semanticModelName, viewFiles, workspaceViewsClient } from '../views/workspaceStore'
import type { Diagnostic, PairedProxy, ProjectFile, PublishViewAnswer, Workspace } from '../types'
import { publishFailure } from './PublishTab'

type ViewFileEntry = ReturnType<typeof viewFiles>[number]
const keyOf = (f: { kind: ViewKind; name: string }) => `${f.kind}-${f.name}`

/** Why a document cannot be published right now, in the words the disabled button carries; null when it can. */
export function publishBlocker(o: { proxies: PairedProxy[] | undefined; proxy: string; canPublish: boolean }): string | null {
  if (!o.canPublish) return 'Publishing needs the approver role on this server.'
  if ((o.proxies?.length ?? 0) === 0) return 'This server is not paired with a Gateway. Link it to one on the console’s Servers page.'
  if (o.proxy === '') return 'Choose the Gateway to publish to.'
  return null
}

/** The findings of the project check that are about dashboards and reports: the files under those folders, errors and warnings. */
export function viewDiagnostics(diagnostics: Diagnostic[]): Diagnostic[] {
  return diagnostics.filter((d) => d.severity !== 'note' && (d.location.file.startsWith('dashboards/') || d.location.file.startsWith('reports/')))
}

export interface DocumentsViewProps {
  files: ViewFileEntry[]
  loading: boolean
  /** Why the files could not be read, if they could not. */
  error: unknown
  /** The address that opens a document in the editor. */
  editHref: (f: ViewFileEntry) => string
  /** Why a document cannot be made (no author role), or null. */
  cannotMake: string | null
  newHref: (kind: ViewKind) => string
}

/** The documents of the workspace and the buttons that start a new one. Hook-free: a test draws it. */
export function DocumentsView(p: DocumentsViewProps) {
  return (
    <div className="card flush" data-testid="build-views-list">
      <div className="row" style={{ gap: 8, padding: '8px 12px', alignItems: 'center', flexWrap: 'wrap' }}>
        <strong>In this workspace</strong>
        {(['dashboard', 'report'] as const).map((kind) => (
          p.cannotMake
            ? <button key={kind} type="button" className="btn btn-sm" disabled title={p.cannotMake} data-testid={`build-views-new-${kind}`}>New {kind}</button>
            : <Link key={kind} to={p.newHref(kind)} className="btn btn-sm" data-testid={`build-views-new-${kind}`}>New {kind}</Link>
        ))}
        {p.cannotMake && <span className="hint" data-testid="build-views-new-why">{p.cannotMake}</span>}
      </div>
      {p.loading && <div className="empty" role="status" data-testid="build-views-loading">Loading…</div>}
      {!p.loading && p.error != null && <div className="empty" role="alert" data-testid="build-views-list-error">The workspace’s files could not be read.</div>}
      {!p.loading && p.error == null && p.files.length === 0 && <div className="empty" data-testid="build-views-empty">This workspace has no dashboards or reports yet. They are the files of its <span className="mono">dashboards/</span> and <span className="mono">reports/</span> folders.</div>}
      {p.files.map((f) => (
        <div key={f.path} className="grid-row" style={{ gridTemplateColumns: '0.7fr 2fr 3fr 0.6fr', gap: 14, alignItems: 'center' }} data-testid={`build-views-doc-${keyOf(f)}`}>
          <span>{f.kind}</span>
          <span className="mono">{f.name}</span>
          <span className="mono">{f.path}</span>
          <Link to={p.editHref(f)} data-testid={`build-views-edit-${keyOf(f)}`}>Edit</Link>
        </div>
      ))}
    </div>
  )
}

export interface PublishViewsProps {
  documents: ViewFileEntry[]
  loading: boolean
  error: unknown
  proxies: PairedProxy[] | undefined
  proxy: string
  canPublish: boolean
  /** The document being published, if one is. */
  busy: string | null
  done: Record<string, PublishViewAnswer>
  failed: Record<string, unknown>
  onProxy: (name: string) => void
  onPublish: (f: ViewFileEntry) => void
}

/** Publishing the documents of the project as merged: each with its button, and the answer of the Gateway under it. Hook-free: a test draws it and presses its buttons. */
export function PublishViewsView(p: PublishViewsProps) {
  const blocker = publishBlocker(p)
  return (
    <div className="card" style={{ marginTop: 14 }} data-testid="build-views-publish">
      <div className="card-body">
        <strong>Publish to a Gateway</strong>
        <div className="hint" style={{ margin: '4px 0 8px' }}>
          Publishes the files of the project <em>as merged</em> (a workspace’s drafts are published after their merge) to the Gateway’s registry, where the Gateway checks them again against the model versions it serves. Publish the semantic model first, on the Publish tab.
        </div>
        <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginBottom: 8 }}>
          <label className="row" style={{ gap: 8 }}>
            <span className="hint">Gateway</span>
            <select className="input" value={p.proxy} onChange={(e) => p.onProxy(e.target.value)} data-testid="build-views-publish-proxy" disabled={(p.proxies?.length ?? 0) === 0}>
              <option value="">Choose…</option>
              {(p.proxies ?? []).map((x) => <option key={x.name} value={x.name}>{x.name}</option>)}
            </select>
          </label>
          {blocker && <span className="hint" data-testid="build-views-publish-why">{blocker}</span>}
        </div>
        {p.loading && <div className="empty" role="status">Loading…</div>}
        {p.error != null && <div className="empty" role="alert" data-testid="build-views-publish-error">The project’s files could not be read.</div>}
        {!p.loading && p.error == null && p.documents.length === 0 && <div className="empty" data-testid="build-views-publish-none">The project has no dashboards or reports yet: merge a workspace that has some.</div>}
        {p.documents.map((f) => {
          const key = keyOf(f)
          return (
            <div key={f.path} style={{ padding: '6px 0', borderTop: '1px solid var(--border, #ddd)' }} data-testid={`build-views-publish-row-${key}`}>
              <div className="row" style={{ gap: 12, alignItems: 'center' }}>
                <span>{f.kind}</span> <span className="mono">{f.path}</span>
                <button type="button" className="btn btn-sm btn-primary" disabled={blocker !== null || p.busy !== null} title={blocker ?? undefined} onClick={() => p.onPublish(f)} data-testid={`build-views-publish-button-${key}`}>{p.busy === key ? 'Publishing…' : 'Publish'}</button>
              </div>
              {p.failed[key] != null && <div className="banner error" role="alert" data-testid={`build-views-publish-failed-${key}`}><span className="mark">!</span><span>{publishFailure(p.failed[key])}</span></div>}
              {p.done[key] && (
                <div className="hint" data-testid={`build-views-publish-done-${key}`}>
                  Published to <strong>{p.done[key].proxy}</strong> as version <span className="mono">{p.done[key].version ?? '?'}</span>{p.done[key].created ? ' (new)' : p.done[key].unchanged ? ' (unchanged)' : ''}.
                  {p.done[key].depends_on && p.done[key].depends_on!.length > 0 && <> Depends on <span className="mono">{p.done[key].depends_on!.join(', ')}</span>.</>}
                </div>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}

/** The findings of the project check about documents, each opening its document. */
export function CheckView({ diagnostics, checked, pending, editHref }: { diagnostics: Diagnostic[]; checked: boolean; pending: boolean; editHref: (file: string) => string | null }) {
  const found = viewDiagnostics(diagnostics)
  return (
    <div className="card flush" style={{ marginTop: 14 }} data-testid="build-views-check">
      <div className="grid-head" style={{ gridTemplateColumns: '1.2fr 1.6fr 3fr', gap: 14 }}><span>Code</span><span>Where</span><span>What</span></div>
      {pending && <div className="empty" role="status" data-testid="build-views-check-pending">Checking the project…</div>}
      {!pending && !checked && <div className="empty" data-testid="build-views-check-idle">Not checked yet. Press Check.</div>}
      {!pending && checked && found.length === 0 && <div className="empty" data-testid="build-views-check-clean">The project check found nothing to fix in the dashboards and reports.</div>}
      {found.map((d, i) => {
        const href = editHref(d.location.file)
        return (
          <div key={`${d.code}-${i}`} className="grid-row" style={{ gridTemplateColumns: '1.2fr 1.6fr 3fr', gap: 14 }} data-testid="build-views-check-finding" data-severity={d.severity}>
            <span className="mono">{d.code}</span>
            <span className="mono">{href ? <Link to={href}>{d.location.file}{d.location.line > 0 ? `:${d.location.line}` : ''}</Link> : d.location.file}</span>
            <span>{d.found}</span>
          </div>
        )
      })}
    </div>
  )
}

const READ_ONLY = 'Saving needs the author role on this server.'

/**
 * The Views tab (plan 32 DBR29): the dashboards and reports of a project are the files of its `dashboards/` and `reports/` folders, edited in a workspace with the shared editor (`@dbdatakit/views-editor`), checked with the project's own
 * models by the project check, and published from the project as merged to a Gateway's registry. Nothing here changes what the server runs until a workspace is merged and a document is published.
 */
export function ViewsTab() {
  const { project } = useOutletContext<{ project: string }>()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const workspace = params.get('workspace')
  const edit = params.get('edit')
  const making = params.get('new')
  const previewId = params.get('preview') ?? ''
  const [newName, setNewName] = useState('')
  const [proxy, setProxy] = useState('')
  const [done, setDone] = useState<Record<string, PublishViewAnswer>>({})
  const [failed, setFailed] = useState<Record<string, unknown>>({})
  const [busy, setBusy] = useState<string | null>(null)

  const base = `/projects/${encodeURIComponent(project)}/views`
  const link = (more: Record<string, string>) => { const q = new URLSearchParams(); if (workspace) q.set('workspace', workspace); if (previewId) q.set('preview', previewId); for (const [k, v] of Object.entries(more)) q.set(k, v); return `${base}?${q.toString()}` }

  const commands = useQuery({ queryKey: ['build', project, 'commands'], queryFn: () => buildApi.commands(project) })
  const canAuthor = commands.data?.find((c) => c.tool === 'project_model_create')?.allowed === true
  const canPublish = commands.data?.some((c) => c.role === 'approver' && c.allowed) === true
  const workspaces = useQuery({ queryKey: ['build', project, 'workspaces'], queryFn: () => buildApi.workspaces(project) })
  const mine = useMemo(() => (workspaces.data ?? []).filter((w) => w.mine), [workspaces.data])
  const files = useQuery({ queryKey: ['build', project, workspace, 'files'], queryFn: () => buildApi.files(project, workspace!), enabled: workspace !== null, refetchOnMount: 'always' })
  const workspaceFiles: ProjectFile[] = files.data?.document?.data.files ?? []
  const documents = viewFiles(workspaceFiles)
  // the project as merged moves when a workspace is merged (on another tab), so it is read again each time this tab opens
  const merged = useQuery({ queryKey: ['build', project, 'files'], queryFn: () => buildApi.projectFiles(project), refetchOnMount: 'always' })
  const mergedDocuments = viewFiles(merged.data?.document?.data.files ?? [])
  const proxies = useQuery({ queryKey: ['build', 'proxies'], queryFn: () => buildApi.proxies() })

  // the models of the workspace, by the name their file gives them: the editor asks of the first when a document has none yet
  const semanticPaths = workspaceFiles.filter((f) => /^semantic\/.+\.ya?ml$/.test(f.path)).map((f) => f.path)
  const models = useQuery({
    queryKey: ['build', project, workspace, 'models', semanticPaths.join('|')],
    enabled: workspace !== null && semanticPaths.length > 0,
    queryFn: async () => (await Promise.all(semanticPaths.map(async (p) => semanticModelName((await buildApi.readFile(project, workspace!, p)).document?.data.content ?? '')))).filter((n): n is string => n !== null),
  })

  const check = useMutation({ mutationFn: () => buildApi.checkDraft(project, workspace!) })
  const diagnostics: Diagnostic[] = check.data?.document?.diagnostics ?? []

  const choose = (id: string | null) => { setParams(id ? { workspace: id } : {}, { replace: true }); check.reset() }
  const make = useMutation({
    mutationFn: () => buildApi.makeWorkspace(project, newName.trim()),
    onSuccess: async (made: Workspace) => { setNewName(''); await queryClient.invalidateQueries({ queryKey: ['build', project, 'workspaces'] }); choose(made.id) },
  })

  const rows = useMemo(() => (previewId ? moduleClient({ baseUrl: `/relay/${encodeURIComponent(previewId)}`, serverCheck: true }) : null), [previewId])
  const editing = useMemo(() => {
    if (!workspace) return null
    const m = /^(dashboard|report)\/(.+)$/.exec(edit ?? '')
    if (m) return { kind: m[1] as ViewKind, name: decodeURIComponent(m[2]!) }
    return making === 'dashboard' || making === 'report' ? { kind: making as ViewKind, name: null } : null
  }, [workspace, edit, making])

  const host = useMemo<EditorHost | null>(() => {
    if (!workspace) return null
    const client = workspaceViewsClient(buildFilesApi({
      files: () => buildApi.files(project, workspace),
      read: (path) => buildApi.readFile(project, workspace, path),
      create: (path, content) => buildApi.createFile(project, workspace, path, content),
      write: (path, content, hash) => buildApi.writeFile(project, workspace, path, content, hash),
      remove: (path, hash) => buildApi.deleteFile(project, workspace, path, hash),
    }), { catalog: (model) => buildApi.modelCatalog(project, workspace, model), rows })
    const q = (more: Record<string, string>) => { const u = new URLSearchParams({ workspace }); if (previewId) u.set('preview', previewId); for (const [k, v] of Object.entries(more)) u.set(k, v); return `${base}?${u.toString()}` }
    return {
      client,
      store: clientStore(client),
      listHref: () => q({}),
      viewHref: () => null,
      editHref: (kind, name) => q({ edit: `${kind}/${encodeURIComponent(name)}` }),
      go: (href, options) => navigate(href, options),
      changed: async () => {
        await queryClient.invalidateQueries({ queryKey: ['build', project, workspace, 'files'] })
        await queryClient.invalidateQueries({ queryKey: ['build', project, workspace, 'git'] })
      },
      fileLabel: (kind, name) => `${FOLDERS[kind]}/${name}.yaml`,
      defaultModel: models.data?.[0],
      readOnly: canAuthor || commands.isLoading ? undefined : READ_ONLY,
      extra: ({ saved, dirty, valid }) => (
        <div className="hint" style={{ margin: '4px 0' }} data-testid="build-views-editor-note">
          {!saved ? 'Not in the workspace yet: name it and Save.' : dirty ? 'Save to write the file in the workspace.' : !valid ? 'The file has problems the loader reports; it cannot be published as it is.' : 'Saved in the workspace. Merge it on the Edit tab, then publish it from the project below.'}
        </div>
      ),
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project, workspace, previewId, rows, models.data, canAuthor, commands.isLoading, navigate, queryClient])

  const publish = async (f: ViewFileEntry) => {
    const key = keyOf(f)
    setBusy(key)
    setFailed((d) => Object.fromEntries(Object.entries(d).filter(([k]) => k !== key)))
    try {
      const answer = await buildApi.publishView(project, proxy, { kind: f.kind, name: f.name })
      setDone((d) => ({ ...d, [key]: answer }))
    } catch (e) {
      setFailed((d) => ({ ...d, [key]: e }))
    } finally { setBusy(null) }
  }

  return (
    <div data-testid="build-views">
      <div className="row" style={{ gap: 12, marginBottom: 14, flexWrap: 'wrap' }}>
        <select value={workspace ?? ''} onChange={(e) => choose(e.target.value || null)} data-testid="build-views-workspace" aria-label="Workspace">
          <option value="">Choose a workspace…</option>
          {mine.map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
        </select>
        <input value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="new workspace name" aria-label="New workspace name" data-testid="build-views-workspace-name" />
        <button type="button" className="btn" disabled={newName.trim() === '' || make.isPending || !canAuthor} title={canAuthor ? undefined : 'Starting a workspace needs the author role on this server.'} onClick={() => make.mutate()} data-testid="build-views-workspace-make">Start a workspace</button>
        {workspace !== null && (
          <label className="row" style={{ gap: 8 }}>
            <span className="hint">Rows from</span>
            <select value={previewId} onChange={(e) => { const next = new URLSearchParams(params); if (e.target.value) next.set('preview', e.target.value); else next.delete('preview'); setParams(next, { replace: true }) }} data-testid="build-views-preview-source">
              <option value="">No rows (editing only)</option>
              {(proxies.data ?? []).map((x) => <option key={x.name} value={x.instance_id}>{x.name}</option>)}
            </select>
          </label>
        )}
      </div>
      <ErrorBanner error={make.error ?? workspaces.error} />
      {workspace === null && <div className="empty" data-testid="build-views-no-workspace">Choose or start a workspace to edit the project’s dashboards and reports. The documents of the project as merged can be published below.</div>}
      {workspace !== null && editing && host && <DashboardEditor key={`${workspace}/${editing.kind}/${editing.name ?? ''}`} kind={editing.kind} name={editing.name} host={host} />}
      {workspace !== null && !editing && (
        <>
          <DocumentsView files={documents} loading={files.isLoading} error={files.error ?? (files.data && !files.data.document?.ok ? new Error('refused') : null)}
            editHref={(f) => link({ edit: `${f.kind}/${encodeURIComponent(f.name)}` })} newHref={(kind) => link({ new: kind })} cannotMake={canAuthor || commands.isLoading ? null : READ_ONLY} />
          <div className="row" style={{ gap: 12, marginTop: 14, alignItems: 'center' }}>
            <button type="button" className="btn" disabled={check.isPending} onClick={() => check.mutate()} data-testid="build-views-check-button">{check.isPending ? 'Checking…' : 'Check the project'}</button>
            <span className="hint">Reads every dashboard and report with the project’s own models as the catalog. Nothing is written.</span>
          </div>
          <ErrorBanner error={check.error} />
          <CheckView diagnostics={diagnostics} checked={check.data !== undefined} pending={check.isPending}
            editHref={(file) => { const v = documents.find((d) => d.path === file); return v ? link({ edit: `${v.kind}/${encodeURIComponent(v.name)}` }) : null }} />
        </>
      )}
      <PublishViewsView documents={mergedDocuments} loading={merged.isLoading} error={merged.error} proxies={proxies.data} proxy={proxy} canPublish={canPublish} busy={busy} done={done} failed={failed}
        onProxy={(v) => { setProxy(v); setDone({}); setFailed({}) }} onPublish={(f) => void publish(f)} />
    </div>
  )
}
