import type { AdoptionRequest, WorkspaceEvent } from './api'

const NAME = /^[a-z0-9][a-z0-9_-]{0,31}$/

/** A workspace's name and a project's id are the same shape on the server: lower-case letters, digits, `-` and `_`, at most 32. */
export const isName = (name: string) => NAME.test(name)

/** Why a name would be refused, in a sentence, or null when it is fine (the server says the same; this is for the form as you type). */
export function nameProblem(name: string, what: 'workspace' | 'project'): string | null {
  if (name === '') return `Give the ${what} a name.`
  if (name.length > 32) return 'At most 32 characters.'
  if (!isName(name)) return 'Lower-case letters, digits, "-" and "_", starting with a letter or digit.'
  return null
}

/** What the create button of a form is: whether it may be pressed, and why not when it may not. */
export function createState(input: { name: string; canWrite: boolean; busy: boolean; what: 'workspace' | 'project'; taken?: readonly string[] }): { enabled: boolean; why: string | null } {
  if (!input.canWrite) return { enabled: false, why: input.what === 'project' ? 'Making a project needs the administrator role.' : 'Making a workspace needs the author or administrator role.' }
  const problem = nameProblem(input.name, input.what)
  if (problem) return { enabled: false, why: input.name === '' ? null : problem }
  if (input.taken?.includes(input.name)) return { enabled: false, why: `There is a ${input.what} called "${input.name}" already.` }
  return { enabled: !input.busy, why: null }
}

/** Whether the person may abandon a workspace: only its owner, and only with the author role. */
export function abandonState(input: { mine: boolean; canWrite: boolean }): { enabled: boolean; why: string | null } {
  if (!input.mine) return { enabled: false, why: 'A workspace is abandoned by its owner.' }
  if (!input.canWrite) return { enabled: false, why: 'Abandoning a workspace needs the author or administrator role.' }
  return { enabled: true, why: null }
}

const WORDS: Record<string, string> = { created: 'made', committed: 'committed', pushed: 'pushed', merged: 'merged', abandoned: 'abandoned' }

/** One line of a workspace's history: "Ada committed: Title the grid". */
export function describeEvent(event: WorkspaceEvent): string {
  const verb = WORDS[event.kind] ?? event.kind
  return event.detail ? `${event.actor} ${verb}: ${event.detail}` : `${event.actor} ${verb}`
}

/** A node of the file tree: a folder (with children) or a file (with its size). */
export interface TreeNode { name: string; path: string; folder: boolean; size: number; children: TreeNode[] }

/** The flat list of paths the server sends, as a tree: folders first, each level in name order. */
export function fileTree(files: readonly { path: string; size: number }[]): TreeNode[] {
  const root: TreeNode = { name: '', path: '', folder: true, size: 0, children: [] }
  for (const file of files) {
    let at = root
    const parts = file.path.split('/')
    parts.forEach((part, i) => {
      const path = parts.slice(0, i + 1).join('/')
      const last = i === parts.length - 1
      let next = at.children.find((c) => c.name === part && c.folder === !last)
      if (!next) { next = { name: part, path, folder: !last, size: last ? file.size : 0, children: [] }; at.children.push(next) }
      at = next
    })
  }
  const order = (nodes: TreeNode[]) => {
    nodes.sort((a, b) => (a.folder === b.folder ? a.name.localeCompare(b.name) : a.folder ? -1 : 1))
    nodes.forEach((n) => order(n.children))
  }
  order(root.children)
  return root.children
}

/** The language the editor colours a file in, by its name. */
export function languageOf(path: string): 'yaml' | 'csharp' | 'sql' | 'plaintext' {
  const lower = path.toLowerCase()
  if (lower.endsWith('.yaml') || lower.endsWith('.yml')) return 'yaml'
  if (lower.endsWith('.sql')) return 'sql'
  if (lower.endsWith('.cs') || lower.endsWith('.csx')) return 'csharp'
  return 'plaintext'
}

/** Why a path would be refused as you type it (the server checks it again, and says its own words): inside the workspace, no `.git`, not a folder's name. */
export function pathProblem(path: string): string | null {
  if (path === '') return null
  if (path.length > 240) return 'A path is at most 240 characters.'
  if (/[\\:]/.test(path)) return 'A path is written with "/" and has no drive or ":" in it.'
  if (path.startsWith('/')) return 'A path is relative to the workspace: it does not start with "/".'
  const parts = path.split('/')
  if (parts.some((p) => p === '')) return 'A path has no empty part.'
  if (parts.some((p) => p === '.' || p === '..')) return 'A path stays inside the workspace: no "." or ".." parts.'
  if (parts.some((p) => p.replace(/[ .]+$/, '').toLowerCase() === '.git')) return 'Git\'s own files (.git) are not files of the workspace.'
  if (parts.some((p) => /[ .]$/.test(p))) return 'A part of a path does not end with a space or a ".".'
  return null
}

/** What the file buttons are: Save needs a change, and the owner with the author role; the reason is shown when it is not that. */
export function saveState(input: { mine: boolean; canWrite: boolean; dirty: boolean; busy: boolean }): { enabled: boolean; why: string | null } {
  if (!input.mine) return { enabled: false, why: 'A workspace is edited by its owner.' }
  if (!input.canWrite) return { enabled: false, why: 'Editing needs the author or administrator role.' }
  return { enabled: input.dirty && !input.busy, why: null }
}

/** Commit: needs a message and something no commit has taken yet. */
export function commitState(input: { mine: boolean; canWrite: boolean; message: string; uncommitted: boolean; busy: boolean }): { enabled: boolean; why: string | null } {
  if (!input.mine) return { enabled: false, why: 'A workspace is committed by its owner.' }
  if (!input.canWrite) return { enabled: false, why: 'Committing needs the author or administrator role.' }
  if (!input.uncommitted) return { enabled: false, why: 'Nothing has changed since the last commit.' }
  return { enabled: input.message.trim() !== '' && !input.busy, why: null }
}

/** Merge: a commit has to be there and nothing left uncommitted (a merge takes what is committed). The approver role is the server's to judge, and its refusal is shown in its own words. */
export function mergeState(input: { commits: number; uncommitted: boolean; busy: boolean }): { enabled: boolean; why: string | null } {
  if (input.commits === 0) return { enabled: false, why: 'Commit a change first: a merge takes what is committed.' }
  if (input.uncommitted) return { enabled: false, why: 'There are changes no commit has taken; commit them before merging.' }
  return { enabled: !input.busy, why: null }
}

/** The files a refused merge names, when the server sent them with its 409. */
export function conflictsOf(body: unknown): string[] {
  const conflicts = (body as { conflicts?: unknown } | null | undefined)?.conflicts
  return Array.isArray(conflicts) ? conflicts.filter((c): c is string => typeof c === 'string') : []
}

/** A status the server reports for a change, as a word. */
export function statusWord(status: string): string {
  const map: Record<string, string> = { A: 'added', M: 'changed', D: 'deleted', R: 'renamed', added: 'added', modified: 'changed', deleted: 'deleted', renamed: 'renamed' }
  return map[status] ?? status.toLowerCase()
}

// ---- builds, preview and publish (plan 22a GP35b) ----

const BUILD_WORDS: Record<string, string> = { queued: 'waiting', running: 'building', succeeded: 'built', failed: 'failed', canceled: 'canceled' }

/** A build's status as a person reads it. */
export const buildWord = (status: string) => BUILD_WORDS[status] ?? status

/** Whether a build is still going (the list is polled while one is). */
export const buildBusy = (status: string) => status === 'queued' || status === 'running'

/** What the Build button is: the author role, a server that can build, and no build already going for this draft. */
export function buildButton(input: { canWrite: boolean; ready: boolean; problems: readonly string[]; busy: boolean; closed?: boolean }): { enabled: boolean; why: string | null } {
  if (input.closed) return { enabled: false, why: 'This workspace was merged, so it cannot be built again; its builds can still be previewed and published.' }
  if (!input.canWrite) return { enabled: false, why: 'Building needs the author or administrator role.' }
  if (!input.ready) return { enabled: false, why: input.problems[0] ?? 'This server cannot build yet.' }
  if (input.busy) return { enabled: false, why: 'A build of this workspace is already going.' }
  return { enabled: true, why: null }
}

/** The note a build makes when it ran with no network isolation (see `WorkspaceAppSettings`): the screen says it in words of its own as well. */
export const notIsolated = (notes: readonly string[]) => notes.some((n) => n.includes('without network isolation'))

/** A diagnostic as one line: `src/main.tsx:3` and the reason, or only the reason. */
export const diagnosticLine = (d: { file?: string | null; line?: number | null; message: string; code?: string }) => (d.file ? `${d.file}${d.line ? `:${d.line}` : ''}: ${d.code ? d.code + ' ' : ''}${d.message}` : `${d.code ? d.code + ' ' : ''}${d.message}`)

/** The product a target is, in words: `Runner`, `Gateway` (a target from before products is a Gateway). */
export const productWord = (product: string | null | undefined) => (product === 'runner' ? 'Runner' : product === 'gateway' || !product ? 'Gateway' : product)

/** What the Publish button is: the approver role (the console's administrator holds it), a succeeded build and a target the server can publish to; a Runner also needs the channel the package goes to (plan 33 SC22c). */
export function publishButton(input: { canWrite: boolean; status: string; target: string; targets: readonly { name: string; can_publish: boolean; product?: string | null; accepts?: boolean | null }[]; busy: boolean; channel?: string }): { enabled: boolean; why: string | null } {
  if (!input.canWrite) return { enabled: false, why: 'Publishing needs the approver or administrator role.' }
  if (input.status !== 'succeeded') return { enabled: false, why: 'Only a build that succeeded is published.' }
  if (input.target.trim() === '') return { enabled: false, why: input.targets.length === 0 ? 'No publisher key is set up: an administrator adds one under Publisher keys.' : null }
  const known = input.targets.find((t) => t.name === input.target.trim())
  if (known && !known.can_publish) return { enabled: false, why: `The key ${known.name} is not paired with a ${productWord(known.product)} that lets it publish.` }
  if (known?.accepts === false) return { enabled: false, why: `This kind of project is not published to a ${productWord(known.product)}; choose a key paired with the product it goes to.` }
  if (known?.product === 'runner' && (input.channel ?? '').trim() === '') return { enabled: false, why: 'Name the channel of the Runner the package goes to.' }
  return { enabled: !input.busy, why: null }
}

/** `slot=connection, slot2=connection2` as the object the publish sends, or the first thing wrong with the text (a pair with no `=`, an empty side). Blank is no binding. */
export function parseBindings(text: string): { bind: Record<string, string>; problem: string | null } {
  const bind: Record<string, string> = {}
  for (const part of text.split(/[,\n]/).map((p) => p.trim()).filter((p) => p !== '')) {
    const eq = part.indexOf('=')
    if (eq <= 0 || eq === part.length - 1) return { bind: {}, problem: `"${part}" is not slot=connection.` }
    bind[part.slice(0, eq).trim()] = part.slice(eq + 1).trim()
  }
  return { bind, problem: null }
}

/** The data source(s) a published model is on: a Store model's one, or the distinct data sources a Build model's sources map to. */
const modelDataSources = (m: { data_source?: string; data_sources?: Record<string, string> }) => m.data_source ?? [...new Set(Object.values(m.data_sources ?? {}))].join(', ')

/** The refusal of a publish that asks for an adoption (SC21f), read from the error's body: the data source and who made it, or null for any other refusal. */
export function adoptionRequest(body: unknown): AdoptionRequest | null {
  const a = typeof body === 'object' && body ? (body as { adopt?: unknown }).adopt : null
  if (typeof a !== 'object' || a === null) return null
  const { data_source, made_by } = a as { data_source?: unknown; made_by?: unknown }
  return typeof data_source === 'string' && data_source !== '' ? { data_source, made_by: typeof made_by === 'string' ? made_by : 'hand' } : null
}

/** What the confirmation says: the data source, who made it, and what adopting means (the Gateway records who adopted it and when). */
export function adoptionSentence(a: AdoptionRequest): string {
  const who = a.made_by === 'hand' ? 'by hand or by an administrator of the Gateway' : `by ${a.made_by}`
  return `The data source ${a.data_source} on the Gateway was made ${who}, not by this Workspace. Publishing puts your model on it. The Gateway records that you adopted it (who, when and which data source) in its audit and in the data source's metadata.`
}

/** What the refresh button says: the data source it reads, or the connection that stands for it. */
export const refreshLabel = (connection: string, dataSource: string) => `Refresh catalog from ${dataSource.trim() || connection.trim() || 'a data source'}`

/** Whether the refresh can be asked, and why not: the author role, a Gateway key and the connection to cache. */
export function refreshButton(input: { canWrite: boolean; target: string; connection: string; busy: boolean; targets: readonly { name: string; can_publish: boolean; product?: string | null }[] }): { enabled: boolean; why: string | null } {
  if (!input.canWrite) return { enabled: false, why: 'Refreshing the catalog changes the draft, which needs the author role.' }
  if (input.busy) return { enabled: false, why: null }
  const gateways = input.targets.filter((t) => t.product === 'gateway' && t.can_publish)
  if (gateways.length === 0) return { enabled: false, why: 'No publisher key is paired with a Gateway: an administrator adds one under Publisher keys. The Gateway holds the logins and answers; the Workspace never connects to a database.' }
  const known = input.targets.find((t) => t.name === input.target.trim())
  if (input.target.trim() === '') return { enabled: false, why: null }
  if (!known || known.product !== 'gateway' || !known.can_publish) return { enabled: false, why: `The catalog is read from a Gateway: choose a key paired with one (${gateways.map((g) => g.name).join(', ')}).` }
  if (input.connection.trim() === '') return { enabled: false, why: 'Name the connection of the project the catalog is for.' }
  return { enabled: true, why: null }
}

/** What a refresh came to, as one sentence. */
export const refreshedLine = (a: { connection: string; data_source: string; target: string; tables: number; columns: number; file: string }) =>
  `Cached ${a.tables} ${a.tables === 1 ? 'table' : 'tables'} and ${a.columns} ${a.columns === 1 ? 'column' : 'columns'} of ${a.connection} from the data source ${a.data_source} on ${a.target} into ${a.file}. No row was read.`

/** The confirmation before the catalog of a data source made by hand is read (SC20j, the same consent as SC21f): the data source, who made it, and that the Gateway records it. */
export function catalogAdoptionSentence(a: AdoptionRequest): string {
  const who = a.made_by === 'hand' ? 'by hand or by an administrator of the Gateway' : `by ${a.made_by}`
  return `The data source ${a.data_source} on the Gateway was made ${who}, not by this Workspace. Reading its catalog gives you its table and column names and types, never rows. The Gateway records that you adopted it (who, when and which data source) in its audit and in the data source's metadata.`
}

/** What a publish came to, as one sentence, whatever it was: an app on a Gateway, a package staged or served on a Runner's channel, models on a Gateway's data sources. */
export function publishedLine(p: { app?: string; instance?: string; version?: number; gateway?: string; package?: string; channel?: string; mode?: string; changed?: boolean; runner?: string; models?: { model: string; data_source?: string; data_sources?: Record<string, string>; version?: number | null; unchanged?: boolean | null; adopted?: string | string[] | null }[] }): string {
  if (p.package) return `${p.mode === 'serve' ? 'Published' : 'Staged'} ${p.package} on ${p.channel} of ${p.runner ?? 'the Runner'} as version ${p.version}${p.changed === false ? ' (the same content as before, nothing changed)' : ''}.`
  if (p.models) return `Published ${p.models.map((m) => `${m.model} on the data source ${modelDataSources(m)}${m.version ? ` as version ${m.version}${m.unchanged ? ' (unchanged)' : ''}` : ''}${m.adopted && [m.adopted].flat().length > 0 ? ` (adopted ${[m.adopted].flat().join(', ')}, made by hand)` : ''}`).join(', ')} to ${p.gateway ?? 'the Gateway'}.`
  if (p.instance) return `Sent ${p.app} to ${p.gateway ?? 'the Gateway'} as the preview of ${p.instance}${p.version ? ` (version ${p.version} of the preview channel)` : ''}.`
  return `Published ${p.app} to ${p.gateway}${p.version ? ` as version ${p.version}` : ''}.`
}

/** The words on the button that makes what a kind publishes: a Build or Sync project is made into a package, a Store project's models are made ready. */
export const makeLabel = (kind: string) => (kind === 'store' ? 'Check and make the models' : kind === 'app' ? 'Build' : kind === 'build' ? 'Check and make the package and the models' : 'Check and make the package')

/** Why a reference to a secret would be refused, or null. The server says the same; a pasted key must not reach it. */
export function referenceProblem(reference: string): string | null {
  if (reference.trim() === '') return null
  if (!reference.trim().startsWith('secret:')) return 'A reference starts with "secret:" and names a secret in the server\'s secret store; the key itself is never typed here.'
  if (/\s/.test(reference.trim())) return 'A reference has no spaces.'
  return null
}

/** What the pair form needs before it is sent: a name, and an address that starts with https://. */
export function pairProblem(input: { name: string; url: string }): string | null {
  if (input.name !== '' && !isName(input.name)) return 'A key\'s name is lower-case letters, digits, "-" and "_", starting with a letter or digit, at most 32.'
  if (input.url.trim() !== '' && !/^https:\/\/\S+$/.test(input.url.trim())) return 'The Gateway\'s address starts with https://.'
  return null
}

/** A finding of a check as one line: `models/marts/orders.yml:29 DDB-101: the reason`; only the code and reason when no place is named. */
export const checkLine = (d: { code: string; message: string; file: string | null; line: number | null }) => `${d.file ? `${d.file}${d.line ? `:${d.line}` : ''} ` : ''}${d.code}: ${d.message}`

/** The one sentence over a check's findings: sound, or how many errors and warnings. Counted here, so the screen and its test say the same. */
export function checkVerdict(result: { diagnostics: readonly { severity: string }[] }): { ok: boolean; text: string } {
  const errors = result.diagnostics.filter((d) => d.severity === 'error').length
  const warnings = result.diagnostics.filter((d) => d.severity === 'warning').length
  if (errors === 0) return { ok: true, text: warnings === 0 ? 'The draft is sound.' : `The draft is sound, with ${warnings} ${warnings === 1 ? 'warning' : 'warnings'}.` }
  return { ok: false, text: `${errors} ${errors === 1 ? 'error' : 'errors'}${warnings > 0 ? ` and ${warnings} ${warnings === 1 ? 'warning' : 'warnings'}` : ''} to fix before this draft is merged or published.` }
}

/** Where a kind publishes, in words: `a Gateway (app)`, `a Runner (package)`; `nowhere yet` for a kind that has no route. */
export const publishesTo = (routes: readonly { product: string; what: string }[]) =>
  routes.length === 0 ? 'nowhere yet' : routes.map((r) => `a ${r.product === 'runner' ? 'Runner' : r.product === 'gateway' ? 'Gateway' : r.product} (${r.what})`).join(' and ')
