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
export interface BuildDiagnostic { file?: string | null; line?: number | null; message: string; code?: string; severity?: string }
export interface BuildResult {
  status: string; message: string; app?: string | null; version?: string | null; entry?: string | null; files?: number; bundled?: string[]; notes?: string[]; diagnostics: BuildDiagnostic[]
  isolated?: boolean; isolation_setting?: string; uncommitted_changes: boolean; commit: string | null; seconds: number
  /** A kind that makes something to publish (plan 33): its name, and what it made (`package`, `tasks`, `files`; `models`, `data_sources`). */
  kind?: string; info?: Record<string, string>
}
/** What was published from a build: when, by whom, to which target, whether it worked and what the Gateway said. */
export interface PublishRecord { at: string; by: string; target: string; ok: boolean; error?: string; answer?: PublishedAnswer; /** the instance of a preview with data, when this record is one (GP35d) */ preview?: string }
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
export interface ProjectKindInfo { name: string; title: string; description: string; buildable: boolean; produces?: boolean; publishes: { product: string; what: string; description: string }[] }
/** One finding of a kind's check of a draft: a code, a severity, and the file and line when the validator names a place. */
export interface CheckDiagnostic { severity: 'error' | 'warning' | 'info'; code: string; message: string; file: string | null; line: number | null }
export interface CheckResult { kind: string; ok: boolean; diagnostics: CheckDiagnostic[] }
/** What a Gateway or a Runner answered a publish with: an app's version, a package's version on a channel, or the models published. */
export interface PublishedAnswer {
  app?: string; version?: number; hash?: string; target?: string; gateway?: string
  /** a preview with data (GP35d): the instance it is of and where the Gateway serves it */
  instance?: string; preview_path?: string | null; preview_address?: string | null
  package?: string; channel?: string; mode?: string; changed?: boolean; uploaded?: number; skipped?: number; tasks?: string[]; runner?: string
  models?: { model: string; data_source: string; version?: number | null; created?: boolean | null; unchanged?: boolean | null }[]
}
export interface PublishAnswer { steps: string[]; published: PublishedAnswer }
/** What sending a build to the Gateway's preview channel answered (`POST .../builds/{b}/preview-data`). */
export interface PreviewDataAnswer { steps: string[]; preview: PublishedAnswer }
/** What a publish says besides the target and the note: for a Runner the channel (and whether it serves at once, the slots to bind), for a Gateway the data source of the models. */
export interface PublishOptions { channel?: string; serve?: boolean; bind?: Record<string, string>; data_source?: string }
/** A target's publisher key as a person may see it: the reference to the secret, never the key, and the Gateway it is paired with. */
export interface PublisherKey { name: string; secret_ref: string; updated: string; gateway: { url: string; roles: string[]; client_name: string; paired: string } | null }

/** One file a compile of a draft makes, against the draft's committed `rendered/`: its text comes with an added or changed one (`truncated` when it was too large to send). */
export interface CompiledFile { path: string; status: 'added' | 'changed' | 'unchanged' | 'removed'; content: string | null; truncated: boolean }
export interface CompileResult { ok: boolean; message: string | null; diagnostics: CheckDiagnostic[]; files: CompiledFile[] }
/** What one model is lowered and rendered to: a file per connection, with its text. */
export interface LoweredResult { ok: boolean; diagnostics: CheckDiagnostic[]; files: { path: string; content: string }[] }
/** One table of a run on generated rows: columns, the rows kept (`row_count` is complete), or the error that stopped it. */
export interface SampleTable { name: string; kind: string; origin: string | null; columns: { name: string; type: string }[]; row_count: number; rows: (string | null)[][]; error: string | null; warnings: string[] }
export interface SampleResult { ok: boolean; diagnostics: CheckDiagnostic[]; data: { tables: SampleTable[] } | null }
export interface SampleOptions { models?: string[]; rows?: number; seed?: number; limit?: number }

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
  /** Compiles the draft on a copy, offline (plan 33 SC20l): what `project compile` would write into `rendered/`, shown against what is committed; nothing in the draft changes. */
  compile: (project: string, id: string) => request<CompileResult>(`${ws(project, id)}/compile`, json({})),
  /** The text one model file is lowered and rendered to, read only. */
  lowered: (project: string, id: string, path: string) => request<LoweredResult>(`${ws(project, id)}/lowered`, json({ path })),
  /** Models run on generated rows, offline: the same seed gives the same rows. */
  sample: (project: string, id: string, options: SampleOptions = {}) => request<SampleResult>(`${ws(project, id)}/sample`, json(options)),
  /** The commits in which one file changed (newest first), and its text as one of them committed it (plan 33 SC21e). */
  fileHistory: (project: string, id: string, path: string) => request<{ path: string; commits: Commit[] }>(`${ws(project, id)}/file-history/${filePath(path)}`).then((r) => r.commits),
  fileAt: (project: string, id: string, sha: string, path: string) => request<{ path: string; sha: string; content: string }>(`${ws(project, id)}/file-at/${enc(sha)}/${filePath(path)}`).then((r) => r.content),
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
  publishTargets: (project?: string) => request<{ targets: PublishTarget[] }>(project ? `/api/v1/publish-targets?project=${enc(project)}` : '/api/v1/publish-targets').then((r) => r.targets),
  publish: (project: string, id: string, build: string, target: string, note: string, options: PublishOptions = {}) => request<PublishAnswer>(`${builds(project, id)}/${enc(build)}/publish`, json({ target, ...(note ? { note } : {}), ...options })),
  previewWithData: (project: string, id: string, build: string, target: string, instance: string) => request<PreviewDataAnswer>(`${builds(project, id)}/${enc(build)}/preview-data`, json({ target, instance })),
  publisherKeys: () => request<{ keys: PublisherKey[] }>('/api/v1/publisher-keys').then((r) => r.keys),
  pairKey: (name: string, url: string, code: string) => request<PublisherKey>(`/api/v1/publisher-keys/${enc(name)}/pair`, json({ url, code })),
  setKey: (name: string, secretRef: string) => request<PublisherKey>(`/api/v1/publisher-keys/${enc(name)}`, { method: 'PUT', body: JSON.stringify({ secret_ref: secretRef }), headers: { 'Content-Type': 'application/json' } }),
  removeKey: (name: string) => request<void>(`/api/v1/publisher-keys/${enc(name)}`, { method: 'DELETE' }),
  events: (project: string, id: string) => request<{ events: WorkspaceEvent[] }>(`/api/v1/projects/${enc(project)}/workspaces/${enc(id)}/events`).then((r) => r.events),
}
