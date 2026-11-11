import { apiUrl, request } from '@dbdatakit/shell'

/** A project the server hosts (`GET /api/v1/projects`): its kind (an app today) and how many workspaces it has now. */
export interface ProjectSummary { id: string; kind: string; title: string | null; workspaces: number; created: string }

/** A workspace: one person's draft of a project. `mine` is whether the signed-in person owns it, and only they abandon it. */
export interface WorkspaceSummary { id: string; name: string; owner: string; mine: boolean; branch: string; base: string; created: string }

/** One thing that happened to a workspace. */
export interface WorkspaceEvent { kind: string; actor: string; detail: string | null; at: string }

/** A file of a draft (`GET .../files`): its path (`/` between the parts) and its size in bytes. */
export interface WorkspaceFile { path: string; size: number }
export interface FileList { files: WorkspaceFile[]; truncated: boolean; max_file_bytes: number }

/** One file that differs from where the draft started (`GET .../changes`): both texts when `sides`, otherwise only the patch (a binary or very large file). */
export interface Change { path: string; status: string; patch: string; before: string | null; after: string | null; sides: boolean }
export interface Changes { changes: Change[]; uncommitted: boolean }
export interface Commit { sha: string; message: string; author: string; when: string }
export interface Pushed { pushed: boolean; branch: string; pull_request_url: string | null }
export interface Merged { merged: boolean; sha: string | null; fast_forward: boolean }

/** A build of a draft (`GET .../builds`): who asked, the commit it was made from, where it is and a sentence (why it failed, or what it made). */
export interface BuildSummary { id: string; workspace: string; status: string; commit: string | null; requested_by: string; message: string | null; requested: string; started: string | null; finished: string | null }
/** One line the bundler said: a file and a line when it names a place (a refused import), else only the reason. */
export interface BuildDiagnostic { file?: string; line?: number; message: string }
export interface BuildResult {
  status: string; message: string; app: string | null; version: string | null; entry: string | null; files: number; bundled: string[]; notes: string[]; diagnostics: BuildDiagnostic[]
  isolated: boolean; isolation_setting: string; uncommitted_changes: boolean; commit: string | null; seconds: number
}
/** What was published from a build: when, by whom, to which target, whether it worked and what the Gateway said. */
export interface PublishRecord { at: string; by: string; target: string; ok: boolean; error?: string; answer?: { app?: string; version?: number; hash?: string; gateway?: string } }
export interface BuildDetail { build: BuildSummary; result: BuildResult | null; publishes: PublishRecord[] }
/** Whether the server can build, and what it lacks (`GET /api/v1/app-build`): each problem is a sentence that says what to set. */
export interface BuildReadiness {
  ready: boolean; problems: string[]
  package_store: { folder: string; packages: number; files: number; digest: string; library: string[] } | null
  bun: { path: string; source: string } | null
  isolation: { setting: string; available: boolean }
}
export interface PublishTarget { name: string; gateway: string | null; product?: string | null; accepts?: boolean | null; can_publish: boolean }

/** A kind of project the server hosts (`GET /api/v1/project-kinds`) and what it publishes as, to which product (plan 33 SC20). */
export interface ProjectKindInfo { name: string; title: string; description: string; buildable: boolean; publishes: { product: string; what: string; description: string }[] }
/** One finding of a kind's check of a draft: a code, a severity, and the file and line when the validator names a place. */
export interface CheckDiagnostic { severity: 'error' | 'warning' | 'info'; code: string; message: string; file: string | null; line: number | null }
export interface CheckResult { kind: string; ok: boolean; diagnostics: CheckDiagnostic[] }
export interface PublishAnswer { steps: string[]; published: { app?: string; version?: number; hash?: string; target?: string; gateway?: string } }
/** A target's publisher key as a person may see it: the reference to the secret, never the key, and the Gateway it is paired with. */
export interface PublisherKey { name: string; secret_ref: string; updated: string; gateway: { url: string; roles: string[]; client_name: string; paired: string } | null }

const enc = encodeURIComponent
/** A file's path as it goes in an address: each part escaped, the `/` between them kept. */
export const filePath = (path: string) => path.split('/').map(enc).join('/')
const ws = (project: string, id: string) => `/api/v1/projects/${enc(project)}/workspaces/${enc(id)}`
const builds = (project: string, id: string) => `${ws(project, id)}/builds`
const json = (body: unknown): RequestInit => ({ method: 'POST', body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } })

/** The Workspace server's calls through the console's relay, as the signed-in person; the server checks the role on every call. */
export const workspaceApi = {
  projects: () => request<{ projects: ProjectSummary[] }>('/api/v1/projects').then((r) => r.projects),
  createProject: (id: string, title: string | null, kind?: string) => request<ProjectSummary>('/api/v1/projects', json({ id, ...(title ? { title } : {}), ...(kind && kind !== 'app' ? { kind } : {}) })),
  projectKinds: () => request<{ kinds: ProjectKindInfo[] }>('/api/v1/project-kinds').then((r) => r.kinds),
  check: (project: string, id: string) => request<CheckResult>(`${ws(project, id)}/check`),
  workspaces: (project: string) => request<{ workspaces: WorkspaceSummary[] }>(`/api/v1/projects/${enc(project)}/workspaces`).then((r) => r.workspaces),
  createWorkspace: (project: string, name: string) => request<WorkspaceSummary>(`/api/v1/projects/${enc(project)}/workspaces`, json({ name })),
  abandon: (project: string, id: string) => request<void>(`/api/v1/projects/${enc(project)}/workspaces/${enc(id)}`, { method: 'DELETE' }),
  files: (project: string, id: string) => request<FileList>(`${ws(project, id)}/files`),
  readFile: (project: string, id: string, path: string) => request<{ path: string; content: string }>(`${ws(project, id)}/files/${filePath(path)}`).then((r) => r.content),
  writeFile: (project: string, id: string, path: string, content: string) => request<{ path: string; created: boolean; size: number }>(`${ws(project, id)}/files/${filePath(path)}`, { method: 'PUT', body: JSON.stringify({ content }) }),
  deleteFile: (project: string, id: string, path: string) => request<void>(`${ws(project, id)}/files/${filePath(path)}`, { method: 'DELETE' }),
  moveFile: (project: string, id: string, from: string, to: string) => request<{ from: string; to: string }>(`${ws(project, id)}/move`, json({ from, to })),
  changes: (project: string, id: string) => request<Changes>(`${ws(project, id)}/changes`),
  history: (project: string, id: string) => request<{ commits: Commit[] }>(`${ws(project, id)}/history`).then((r) => r.commits),
  commit: (project: string, id: string, message: string) => request<{ committed: boolean; sha: string | null }>(`${ws(project, id)}/commit`, json({ message })),
  push: (project: string, id: string) => request<Pushed>(`${ws(project, id)}/push`, { method: 'POST' }),
  merge: (project: string, id: string) => request<Merged>(`${ws(project, id)}/merge`, { method: 'POST' }),
  readiness: () => request<BuildReadiness>('/api/v1/app-build'),
  builds: (project: string, id: string) => request<{ builds: BuildSummary[] }>(builds(project, id)).then((r) => r.builds),
  build: (project: string, id: string, build: string) => request<BuildDetail>(`${builds(project, id)}/${enc(build)}`),
  requestBuild: (project: string, id: string) => request<BuildSummary>(builds(project, id), { method: 'POST' }),
  /** Where a build's preview is served from (through the relay): a page for a frame, with no network and no data. */
  previewUrl: (project: string, id: string, build: string) => apiUrl(`${builds(project, id)}/${enc(build)}/preview/`),
  publishTargets: () => request<{ targets: PublishTarget[] }>('/api/v1/publish-targets').then((r) => r.targets),
  publish: (project: string, id: string, build: string, target: string, note: string) => request<PublishAnswer>(`${builds(project, id)}/${enc(build)}/publish`, json(note ? { target, note } : { target })),
  publisherKeys: () => request<{ keys: PublisherKey[] }>('/api/v1/publisher-keys').then((r) => r.keys),
  pairKey: (name: string, url: string, code: string) => request<PublisherKey>(`/api/v1/publisher-keys/${enc(name)}/pair`, json({ url, code })),
  setKey: (name: string, secretRef: string) => request<PublisherKey>(`/api/v1/publisher-keys/${enc(name)}`, { method: 'PUT', body: JSON.stringify({ secret_ref: secretRef }), headers: { 'Content-Type': 'application/json' } }),
  removeKey: (name: string) => request<void>(`/api/v1/publisher-keys/${enc(name)}`, { method: 'DELETE' }),
  events: (project: string, id: string) => request<{ events: WorkspaceEvent[] }>(`/api/v1/projects/${enc(project)}/workspaces/${enc(id)}/events`).then((r) => r.events),
}
