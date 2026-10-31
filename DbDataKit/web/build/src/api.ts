import { request, type SchemaAssociation, type SchemaNode } from '@dbdatakit/shell'
import type { PairedProxy, PublishAnswer, GitChange, GitCommit, RenderedFiles, BuildProject, FileContent, ProjectFile, Workspace, CommandAnswer, CompareResult, CompileSummary, ConnectionStatus, DeployResult, PlanDetail, PlanEntry, ProjectGraph, SampleResult } from './types'

const project = (id: string) => `/api/v1/projects/${encodeURIComponent(id)}`

/** Runs one command of a project on the server and returns what it answered. A command that reads is run as a viewer; the server decides what the person may run. */
function run<T>(id: string, tool: string, inputs: Record<string, unknown> = {}, workspace?: string) {
  const query = workspace ? `?workspace=${encodeURIComponent(workspace)}` : ''
  return request<CommandAnswer<T>>(`${project(id)}/commands/${tool}${query}`, { method: 'POST', body: JSON.stringify(inputs) })
}

/** The schemas of a project's files and which file each describes (the same for every project). */
export interface FileSchemaDocument {
  associations: SchemaAssociation[]
  schemas: Record<string, SchemaNode>
}

export const buildApi = {
  fileSchemas: () => request<FileSchemaDocument>('/api/v1/schemas'),
  projects: () => request<{ projects: BuildProject[] }>('/api/v1/projects').then((r) => r.projects),
  graph: (id: string) => run<ProjectGraph>(id, 'project_show_graph'),
  /** Checks the project without writing anything: compiles it and says what would change. */
  check: (id: string) => run<CompileSummary>(id, 'project_compile', { check: true }),
  /** Where a connection stands: what exists, what a deploy would do. Reads the target; the connection is the project's default when none is named. */
  status: (id: string, connection?: string) => run<ConnectionStatus>(id, 'connection_status', connection ? { connection } : {}),
  plans: (id: string) => run<{ plans: PlanEntry[] }>(id, 'project_show_plan'),
  plan: (id: string, path: string) => run<PlanDetail>(id, 'project_show_plan', { plan: path }),
  /** Plans a deploy and writes the plan file, then stops: nothing is applied. */
  writePlan: (id: string, connection?: string) => run<DeployResult>(id, 'connection_deploy', { ...(connection ? { connection } : {}), write_plan: true }),
  /** Runs every check of a written plan and prints its statements; executes nothing. */
  dryRun: (id: string, path: string, connection?: string) => run<DeployResult>(id, 'connection_deploy', { ...(connection ? { connection } : {}), apply_plan: path, dry_run: true }),
  /** Applies a written plan. Whoever holds the approver role and sends this is the approver; the server records them. */
  apply: (id: string, path: string, allowances: { risky: boolean; destructive: string[] }, connection?: string) =>
    run<DeployResult>(id, 'connection_deploy', {
      ...(connection ? { connection } : {}),
      apply_plan: path,
      yes: true,
      ...(allowances.risky ? { allow_risky: true } : {}),
      ...(allowances.destructive.length > 0 ? { allow_destructive: allowances.destructive } : {}),
    }),
  /** Compares two tables (of one connection, or of two) by counts and digests; the key defaults to the model's grain. */
  compare: (id: string, o: { connection?: string; table: string; against?: string; againstConnection?: string; key?: string[] }) =>
    run<CompareResult>(id, 'connection_compare', {
      ...(o.connection ? { connection: o.connection } : {}),
      table: o.table,
      ...(o.against ? { against: o.against } : {}),
      ...(o.againstConnection ? { against_connection: o.againstConnection } : {}),
      ...(o.key && o.key.length > 0 ? { key: o.key } : {}),
    }),
  /** The proxies this server is paired with (T10); a connection of a project can be published to any of them. */
  proxies: () => request<{ proxies: PairedProxy[] }>('/api/v1/proxies').then((r) => r.proxies),
  /** Publishes a connection of a project to a paired proxy as a read-only data source (T10, T12): only its read login goes. The server needs the approver role for it; a proxy's refusal comes back in its own words. */
  publish: (id: string, proxy: string, connection: string, dataSource?: string) =>
    request<PublishAnswer>(`${project(id)}/proxies/${encodeURIComponent(proxy)}/publish`, { method: 'POST', body: JSON.stringify({ connection, ...(dataSource ? { data_source: dataSource } : {}) }) }),
  /** The drafts of a project (W02): a git worktree per person and name, where editing happens. */
  workspaces: (id: string) => request<{ workspaces: Workspace[] }>(`${project(id)}/workspaces`).then((r) => r.workspaces),
  makeWorkspace: (id: string, name: string) => request<Workspace>(`${project(id)}/workspaces`, { method: 'POST', body: JSON.stringify({ name }) }),
  abandonWorkspace: (id: string, workspace: string) => request<void>(`${project(id)}/workspaces/${encodeURIComponent(workspace)}`, { method: 'DELETE' }),
  /** The files of a workspace, with their hashes. */
  files: (id: string, workspace: string) => run<{ files: ProjectFile[] }>(id, 'project_file_list', {}, workspace),
  readFile: (id: string, workspace: string, path: string) => run<FileContent>(id, 'project_file_read', { path }, workspace),
  /** Writes a file on the version it was read at; a different current version is refused. */
  writeFile: (id: string, workspace: string, path: string, content: string, hash: string) => run<{ path: string; hash: string }>(id, 'project_file_write', { path, content, expect_hash: hash }, workspace),
  /** Compiles the workspace without writing rendered files: what the compiler finds in the draft. */
  checkDraft: (id: string, workspace: string) => run<CompileSummary>(id, 'project_compile', { check: true }, workspace),
  /** Runs the models on generated rows, offline: what they would produce. */
  sample: (id: string, rows?: number) => run<SampleResult>(id, 'project_sample', rows ? { rows } : {}),
  /** The lowered and rendered text of one model file of a workspace (the compiler's own files, not written anywhere). */
  rendered: (id: string, workspace: string, path: string) => run<RenderedFiles>(id, 'project_compile', { content: true, models: [path] }, workspace),
  /** A few rows of what one model file of a workspace returns on generated data, offline. */
  sampleOf: (id: string, workspace: string, path: string) => run<SampleResult>(id, 'project_sample', { models: [path], rows: 8, limit: 8 }, workspace),
  /** What the draft changes from where it started, with the patch of each file. */
  changes: (id: string, workspace: string) => request<{ changes: GitChange[]; uncommitted: boolean }>(`${project(id)}/workspaces/${encodeURIComponent(workspace)}/changes`),
  history: (id: string, workspace: string) => request<{ commits: GitCommit[] }>(`${project(id)}/workspaces/${encodeURIComponent(workspace)}/history`).then((r) => r.commits),
  commit: (id: string, workspace: string, message: string) => request<{ committed: boolean; sha: string | null }>(`${project(id)}/workspaces/${encodeURIComponent(workspace)}/commit`, { method: 'POST', body: JSON.stringify({ message }) }),
  /** For a project that follows a git remote: sends the draft's branch there. */
  push: (id: string, workspace: string) => request<{ pushed: boolean; branch: string; pull_request_url: string | null }>(`${project(id)}/workspaces/${encodeURIComponent(workspace)}/push`, { method: 'POST' }),
  /** For a project without a remote: merges the draft into the project, for an approver. A conflict changes nothing. */
  merge: (id: string, workspace: string) => request<{ merged: boolean; sha: string }>(`${project(id)}/workspaces/${encodeURIComponent(workspace)}/merge`, { method: 'POST' }),
}
