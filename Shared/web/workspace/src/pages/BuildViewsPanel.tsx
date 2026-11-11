import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { FOLDERS, semanticModelName, viewFileOf, workspaceViewsClient } from '@dbdatakit/build/files'
import { clientStore, type ViewKind } from '@dbdatakit/visuals'
import { DashboardEditor, type EditorHost } from '@dbdatakit/views-editor'
import { workspaceApi, type CheckDiagnostic } from '../api'
import { draftFilesApi, viewFindings, type DraftFiles } from '../buildViews'
import { findingsKey } from './SyncSetPanel'

/** The dashboards and reports among the paths of a draft: dashboards first, each in name order. */
export function viewDocumentsOf(paths: readonly string[]): { kind: ViewKind; name: string; path: string }[] {
  const found = paths.flatMap((p) => { const v = viewFileOf(p); return v ? [v] : [] })
  return found.sort((a, b) => (a.kind === b.kind ? a.name.localeCompare(b.name) : a.kind === 'dashboard' ? -1 : 1))
}

const READ_ONLY = 'Saving needs the author role and is the owner of the workspace\'s.'

/** The documents of the draft with their findings and the buttons that start a new one; hook-free so a test draws it. */
export function ViewDocumentsView({ documents, findings, canWrite, onEdit, onNew }: { documents: readonly { kind: ViewKind; name: string; path: string }[]; findings: readonly CheckDiagnostic[] | null; canWrite: boolean; onEdit: (kind: ViewKind, name: string) => void; onNew: (kind: ViewKind) => void }) {
  return (
    <div data-testid="wsp-views-list">
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginBottom: 6 }}>
        {(['dashboard', 'report'] as const).map((kind) => <button key={kind} type="button" className="btn" disabled={!canWrite} title={canWrite ? undefined : READ_ONLY} data-testid={`wsp-views-new-${kind}`} onClick={() => onNew(kind)}>New {kind}</button>)}
      </div>
      {documents.length === 0 && <div className="empty" data-testid="wsp-views-empty">This draft has no dashboards or reports yet. They are the files of its <code>dashboards/</code> and <code>reports/</code> folders.</div>}
      {documents.map((d) => {
        const own = (findings ?? []).filter((f) => f.file === d.path)
        return (
          <div key={d.path} className="grid-row" style={{ gap: 10, flexWrap: 'wrap' }} data-testid="wsp-views-doc" data-kind={d.kind} data-name={d.name} data-errors={own.filter((f) => f.severity === 'error').length}>
            <span className="hint">{d.kind}</span>
            <button type="button" className="btn-link mono" data-testid="wsp-views-edit" onClick={() => onEdit(d.kind, d.name)}>{d.name}</button>
            <span className="mono hint">{d.path}</span>
            {own.length > 0 && <span className="error" data-testid="wsp-views-bad">{own.length} to look at</span>}
          </div>
        )
      })}
    </div>
  )
}

/** The files of a draft as `DraftFiles`: a missing file reads as null. */
export const draftOf = (project: string, id: string): DraftFiles => ({
  list: async () => (await workspaceApi.files(project, id)).files,
  read: (path) => workspaceApi.readFile(project, id, path).then((t) => t, (e: unknown) => { if (e instanceof ApiError && e.status === 404) return null; throw e }),
  write: async (path, content) => { await workspaceApi.writeFile(project, id, path, content) },
  remove: (path) => workspaceApi.deleteFile(project, id, path),
})

/**
 * The Views tab of a Build project's workspace (plan 33 SC20n): the project's `dashboards/` and `reports/` as files of the draft, edited with the shared editor of `@dbdatakit/views-editor` over the draft (the same editor Build's Views tab and the console have),
 * checked by the draft's check (the findings of those folders are listed). Publishing a dashboard to a Gateway is not here: it goes with the project, as Build's Views tab does it.
 */
export function BuildViewsPanel({ project, id, paths, canWrite }: { project: string; id: string; paths: readonly string[]; canWrite: boolean }) {
  const client = useQueryClient()
  const [editing, setEditing] = useState<{ kind: ViewKind; name: string | null } | null>(null)
  const documents = viewDocumentsOf(paths)
  const check = useMutation({ mutationFn: () => workspaceApi.check(project, id) })
  const semantic = paths.filter((p) => /^semantic\/.+\.ya?ml$/.test(p))
  const models = useQuery({
    queryKey: ['workspace', 'semantic-names', project, id, semantic],
    enabled: semantic.length > 0,
    queryFn: async () => (await Promise.all(semantic.map(async (p) => semanticModelName(await workspaceApi.readFile(project, id, p).catch(() => ''))))).filter((n): n is string => n !== null),
  })
  const viewsClient = useMemo(() => workspaceViewsClient(draftFilesApi(draftOf(project, id)), {}), [project, id])
  const host = useMemo<EditorHost>(() => ({
    client: viewsClient,
    store: clientStore(viewsClient),
    listHref: () => '#list',
    viewHref: () => null,
    editHref: (kind, name) => `#edit/${kind}/${encodeURIComponent(name)}`,
    go: (href) => {
      const m = /^#edit\/(dashboard|report)\/(.+)$/.exec(href)
      setEditing(m ? { kind: m[1] as ViewKind, name: decodeURIComponent(m[2]!) } : null)
    },
    changed: async () => {
      await client.invalidateQueries({ queryKey: ['workspace', 'files', project, id] })
      await client.invalidateQueries({ queryKey: ['workspace', 'changes', project, id] })
      await client.invalidateQueries({ queryKey: ['workspace', 'file', project, id] })
      await client.invalidateQueries({ queryKey: findingsKey(project, id) })
    },
    fileLabel: (kind, name) => `${FOLDERS[kind]}/${name}.yaml`,
    defaultModel: models.data?.[0],
    readOnly: canWrite ? undefined : READ_ONLY,
    extra: ({ saved, dirty }) => <div className="hint" style={{ margin: '4px 0' }} data-testid="wsp-views-editor-note">{!saved ? 'Not in the draft yet: name it and Save.' : dirty ? 'Save to write the file into the draft.' : 'Saved in the draft. Commit and merge it with the project from the Changes tab.'}</div>,
  }), [viewsClient, client, project, id, models.data, canWrite])
  const findings = check.data ? viewFindings(check.data.diagnostics) : null
  return (
    <div data-testid="wsp-views">
      {editing
        ? <>
            <button type="button" className="btn" style={{ marginBottom: 8 }} data-testid="wsp-views-back" onClick={() => setEditing(null)}>Back to the list</button>
            <DashboardEditor key={`${editing.kind}/${editing.name ?? ''}`} kind={editing.kind} name={editing.name} host={host} />
          </>
        : <>
            <ViewDocumentsView documents={documents} findings={findings} canWrite={canWrite} onEdit={(kind, name) => setEditing({ kind, name })} onNew={(kind) => setEditing({ kind, name: null })} />
            <div className="row" style={{ gap: 8, margin: '12px 0 4px', alignItems: 'center' }}>
              <button type="button" className="btn" disabled={check.isPending} data-testid="wsp-views-check" onClick={() => check.mutate()}>{check.isPending ? 'Checking...' : 'Check the draft'}</button>
              <span className="hint">Reads every dashboard and report with the project's own models. Nothing is written.</span>
            </div>
            <ErrorBanner error={check.error} />
            {findings && findings.length === 0 && <div className="hint" data-testid="wsp-views-clean">The check found nothing to fix in the dashboards and reports.</div>}
            {(findings ?? []).map((d, i) => <div key={i} className={d.severity === 'error' ? 'error' : 'hint'} data-testid="wsp-views-finding" data-code={d.code}>{d.file}{d.line ? `:${d.line}` : ''} {d.code}: {d.message}</div>)}
            <div className="hint" style={{ marginTop: 10 }}>The editor has no model catalog here (it comes from a running Build server); the wells take typed field names. Publishing a dashboard to a Gateway stays with Build's Views tab, as the project's data sources do (SC20e).</div>
          </>}
    </div>
  )
}
