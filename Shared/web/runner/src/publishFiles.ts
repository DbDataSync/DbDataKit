import { ApiError } from '@dbdatakit/shell'

/** What the browser gives for a chosen file: enough to name it, size it and read it. A test makes these without a browser. */
export interface FileLike {
  name: string
  size: number
  /** `folder/sub/file` when the file came from choosing a folder (the folder's own name first). */
  webkitRelativePath?: string
  arrayBuffer(): Promise<ArrayBuffer>
  text(): Promise<string>
}

/** One file of the package: its path inside the package (forward slashes, no folder name in front), kept with the file so it can be read again to send it. */
export interface PackageFile { path: string; file: FileLike }

export interface HashedFile extends PackageFile { sha256: string; size: number }

export const MANIFEST_NAMES = ['package.yaml', 'package.yml']

/** The name rule of packages and channels on a runner (`PackageManifestValidator.NameRule`): lower case letters and digits with `_` and `-`, at most 64, not starting with a symbol. */
export const NAME_RULE = /^[a-z0-9][a-z0-9_-]{0,63}$/

/** A path inside the package: the chosen folder's own name is dropped (the files are the folder's content, as `package publish <manifest> <directory>` sends them). */
export function packagePath(file: Pick<FileLike, 'name' | 'webkitRelativePath'>): string {
  const relative = file.webkitRelativePath
  if (!relative) return file.name
  const slash = relative.indexOf('/')
  return slash < 0 ? relative : relative.slice(slash + 1)
}

export const packageFiles = (list: FileLike[]): PackageFile[] => list.map((file) => ({ path: packagePath(file), file })).sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))

/** The manifest inside the folder: a `package.yaml` at its top. It is the manifest, not a file of the package. */
export const manifestIn = (files: PackageFile[]): PackageFile | undefined => files.find((f) => MANIFEST_NAMES.includes(f.path))

export type ManifestRead = { ok: true; name: string } | { ok: false; problem: string }

/** What can be told of a manifest without the runner's validator: that it is `package/1` and names the package. The runner reads it fully and refuses what is wrong, in its own words. */
export function readManifest(text: string): ManifestRead {
  if (text.trim() === '') return { ok: false, problem: 'The manifest is empty.' }
  const schema = /^schema:\s*(\S+)/m.exec(text)?.[1]?.replace(/^["']|["']$/g, '')
  if (schema !== 'package/1') return { ok: false, problem: schema ? `The manifest is "${schema}", not package/1.` : 'The manifest has no schema line: schema: package/1.' }
  const name = /^name:\s*(\S+)/m.exec(text)?.[1]?.replace(/^["']|["']$/g, '')
  if (!name) return { ok: false, problem: 'The manifest does not name the package (a line name: ...).' }
  if (!NAME_RULE.test(name)) return { ok: false, problem: `"${name}" is not a package name: lower case letters, digits, _ and -, starting with a letter or digit.` }
  return { ok: true, name }
}

const hex = (bytes: ArrayBuffer) => [...new Uint8Array(bytes)].map((b) => b.toString(16).padStart(2, '0')).join('')

/** The SHA-256 of the file's bytes, as 64 lower case hexadecimal digits (the browser's `crypto.subtle`, which needs a secure context: the console is served over https). */
export async function sha256Of(file: Pick<FileLike, 'arrayBuffer'>): Promise<string> {
  return hex(await crypto.subtle.digest('SHA-256', await file.arrayBuffer()))
}

/** Hashes the files one after another (only one is in memory at a time), telling how far it is. */
export async function hashFiles(files: PackageFile[], onProgress?: (done: number) => void): Promise<HashedFile[]> {
  const out: HashedFile[] = []
  for (const f of files) {
    out.push({ ...f, sha256: await sha256Of(f.file), size: f.file.size })
    onProgress?.(out.length)
  }
  return out
}

/** The files to ask the runner about, once each: the first path of every distinct content. */
export function distinctContent(files: HashedFile[]): HashedFile[] {
  const seen = new Set<string>()
  return files.filter((f) => !seen.has(f.sha256) && seen.add(f.sha256))
}

export interface PublishForm {
  channel: string
  /** Serve at once instead of staging the version for the runners to prepare. */
  serve: boolean
  note: string
}

export const emptyPublishForm: PublishForm = { channel: '', serve: false, note: '' }

export interface Prepared {
  manifest: string
  name: string
  files: HashedFile[]
}

/** Why Publish cannot be pressed, in words; null when it can. The runner decides again: this only says what is missing, or what is known to be refused. */
export function publishBlock(canPublish: boolean, form: PublishForm, folder: { count: number; hashing: boolean } | null, manifest: ManifestRead | null, prepared: Prepared | null): string | null {
  if (!canPublish) return 'Publishing needs the admin role on this server.'
  if (folder === null || folder.count === 0) return 'Choose the folder of the package.'
  if (manifest === null) return 'The folder has no package.yaml: choose the manifest too.'
  if (!manifest.ok) return manifest.problem
  if (folder.hashing || prepared === null) return 'Reading the files...'
  if (form.channel.trim() === '') return 'Name the channel.'
  if (!NAME_RULE.test(form.channel.trim())) return `"${form.channel.trim()}" is not a channel name: lower case letters, digits, _ and -.`
  return null
}

/** The body of the publish call. The files are named by their hash: the runner has them already or got them before this call. */
export function publishRequest(prepared: Prepared, form: PublishForm) {
  return {
    manifest: prepared.manifest,
    channel: form.channel.trim(),
    mode: form.serve ? 'serve' : 'stage',
    artifacts: prepared.files.map((f) => ({ path: f.path, sha256: f.sha256, size: f.size })),
    ...(form.note.trim() === '' ? {} : { note: form.note.trim() }),
  }
}

/** The step of the publish that is going on, for the progress line. */
export type Step = { kind: 'checking'; done: number; total: number } | { kind: 'sending'; done: number; total: number; path: string } | { kind: 'publishing' }

export const stepText = (step: Step): string =>
  step.kind === 'publishing' ? 'Publishing the version...'
    : step.kind === 'checking' ? `Asking the runner which files it has (${step.done} of ${step.total})...`
      : `Sending ${step.path} (${step.done + 1} of ${step.total})...`

/** What the publish of a package answered (`PUT definitions/integration/{name}`) with the counts of the files. */
export interface Published {
  kind: string
  name: string
  channel: string
  version: number
  changed: boolean
  mode: string
  signature: string
  tasks: string[]
  uploaded: number
  skipped: number
}

/** The refusal in words: the runner's own, with the step it came at. A file that failed names itself; transport failures say the runner did not answer. */
export function refusalText(error: unknown, doing: string): string {
  if (error instanceof ApiError) return error.status === 401 ? 'Sign in again: the session ended.' : `The runner refused ${doing} (${error.status}): ${error.message}`
  return `The runner could not be reached while ${doing}. Try again; the files it already has are not sent again.`
}

/** The size in words. */
export const sizeText = (bytes: number) => (bytes < 1024 ? `${bytes} bytes` : bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`)

/** What the publish needs of the runner: whether it holds a file, the file's upload and the publish. The screen gives the real calls, a test gives fakes. */
export interface PublishApi {
  has(sha256: string): Promise<boolean>
  put(sha256: string, file: FileLike): Promise<unknown>
  publish(name: string, body: ReturnType<typeof publishRequest>): Promise<{ version: number; changed: boolean; mode: string; signature: string; tasks: string[]; channel: string; name: string; kind: string }>
}

/** A publish that stopped: what was being done, and the words to show (the runner's own when it refused). */
export class PublishFailure extends Error {
  doing: string

  constructor(message: string, doing: string) {
    super(message)
    this.name = 'PublishFailure'
    this.doing = doing
  }
}

/**
 * Sends a package the way `dbdatarunner package publish` does: asks the runner (HEAD) for each distinct file, sends (PUT) only those it does not have, then publishes the version naming every file by its hash. Stops at the first refusal with the
 * words of the step: a file that was sent stays on the runner, so a second try does not send it again.
 */
export async function sendPackage(api: PublishApi, prepared: Prepared, form: PublishForm, onStep?: (step: Step) => void): Promise<Published> {
  const distinct = distinctContent(prepared.files)
  const missing: HashedFile[] = []
  let checked = 0
  for (const f of distinct) {
    onStep?.({ kind: 'checking', done: checked, total: distinct.length })
    const doing = `to say whether it has ${f.path}`
    let has: boolean
    try { has = await api.has(f.sha256) } catch (e) { throw new PublishFailure(refusalText(e, doing), doing) }
    if (!has) missing.push(f)
    checked++
  }

  let sent = 0
  for (const f of missing) {
    onStep?.({ kind: 'sending', done: sent, total: missing.length, path: f.path })
    const doing = `the file ${f.path}`
    try { await api.put(f.sha256, f.file) } catch (e) { throw new PublishFailure(refusalText(e, doing), doing) }
    sent++
  }

  onStep?.({ kind: 'publishing' })
  const doing = `the package ${prepared.name}`
  try {
    const answer = await api.publish(prepared.name, publishRequest(prepared, form))
    return { ...answer, uploaded: missing.length, skipped: prepared.files.length - missing.length }
  } catch (e) {
    throw new PublishFailure(refusalText(e, doing), doing)
  }
}
