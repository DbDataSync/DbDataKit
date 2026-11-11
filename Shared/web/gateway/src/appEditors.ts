import { NEEDS_PUBLISHER, NEEDS_APPROVER } from './apps'
import type { AppAbout, AppCheck, AppDetail, AppInstanceRow, AppsCaller, NavigationSetRow } from './types'

// The logic of the Apps screen's editors (plan 33, UE01 to UE03), apart from the screens so it can be tested: the instance form, the package a folder becomes, the navigation set's member list. Nothing here is a second
// validator. The Gateway reads every name, setting, binding and manifest again and its refusal is what the screen shows; these functions only say why a button is off before a call that could not mean anything, and build
// the request a call sends.

// ---- UE01: an app instance ----

export interface SettingRow { key: string; value: string }

/** What the instance form holds. `pin` is a version number as text, or '' to follow what the app serves. */
export interface InstanceForm {
  name: string
  app: string
  pin: string
  title: string
  settings: SettingRow[]
  /** The model instance typed for each model, by model name (an empty text is "not bound"). */
  bindings: Record<string, string>
}

/** The body of `PUT /api/v1/app-instances/{name}`. */
export interface InstanceRequest {
  app: string
  pin: number | null
  title: string | null
  settings: Record<string, string>
  bindings: Record<string, string>
}

/** Settings the host reads; offered as suggestions in the key field, never required. Any other key is the app's own. */
export const KNOWN_SETTINGS: { key: string; hint: string }[] = [
  { key: 'embed.allowed_origins', hint: 'Origins that may frame this instance, comma-separated, such as https://portal.example.com. With none, nothing may.' },
  { key: 'portal', hint: 'The portal instance whose look and menu this instance is shown in.' },
]

export const emptyInstanceForm = (app = ''): InstanceForm => ({ name: '', app, pin: '', title: '', settings: [], bindings: {} })

export const formFromRow = (row: Pick<AppInstanceRow, 'name' | 'app' | 'pin' | 'title' | 'settings' | 'bindings'>): InstanceForm => ({
  name: row.name,
  app: row.app,
  pin: row.pin === null ? '' : String(row.pin),
  title: row.title ?? '',
  settings: Object.entries(row.settings ?? {}).map(([key, value]) => ({ key, value })),
  bindings: { ...row.bindings },
})

/** The models the version an instance would run uses, in the order the manifest lists them: the pinned version's, or the one the app serves when it follows. Null while the app's detail is not read yet, or it has no such version or its manifest is unreadable: the form does not save then, because saving would drop bindings it cannot match to a model. */
export function modelsOf(detail: Pick<AppDetail, 'serving' | 'versions'> | undefined, pin: string): string[] | null {
  if (!detail) return null
  const wanted = pin === '' ? detail.serving : Number(pin)
  const about = detail.versions.find((v) => v.version === wanted)?.about
  if (!about) return null
  const seen: string[] = []
  for (const m of about.models) if (!seen.includes(m.model)) seen.push(m.model)
  return seen
}

export interface BindingRow { model: string; value: string; /** False for a binding the version no longer has a model for: it is shown, and saving drops it. */ used: boolean }

/** A row for each model the version uses, then the bindings the instance has for a model it does not use. A binding for a model the manifest does not list is never offered for a new entry. */
export function bindingRows(models: string[], bindings: Record<string, string>): BindingRow[] {
  const rows: BindingRow[] = models.map((model) => ({ model, value: bindings[model] ?? '', used: true }))
  for (const model of Object.keys(bindings).sort()) if (!models.includes(model)) rows.push({ model, value: bindings[model]!, used: false })
  return rows
}

/** Why the form cannot be saved yet, or null. Only what makes the call meaningless: the Gateway checks names, limits and the rest and says so in its own words. */
export function instanceFormProblem(form: InstanceForm, isNew: boolean, existing: string[]): string | null {
  if (form.name.trim() === '') return 'Name the instance: it is the last part of its address.'
  if (isNew && existing.includes(form.name.trim())) return `There is already an instance named ${form.name.trim()}; choose another name, or edit that one.`
  if (form.app === '') return 'Choose the app this instance runs.'
  const keys = form.settings.map((s) => s.key.trim())
  if (keys.some((k) => k === '')) return 'A setting needs a name; remove the empty row or name it.'
  const repeated = keys.find((k, i) => keys.indexOf(k) !== i)
  if (repeated) return `The setting ${repeated} is listed twice.`
  return null
}

/** The request for the form: blank title is none, blank setting values stay (an empty value is a value), bindings for models the version does not use and empty bindings are left out. */
export function instanceRequest(form: InstanceForm, models: string[]): InstanceRequest {
  const bindings: Record<string, string> = {}
  for (const model of models) {
    const value = (form.bindings[model] ?? '').trim()
    if (value !== '') bindings[model] = value
  }
  const settings: Record<string, string> = {}
  for (const row of form.settings) settings[row.key.trim()] = row.value
  return { app: form.app, pin: form.pin === '' ? null : Number(form.pin), title: form.title.trim() === '' ? null : form.title.trim(), settings, bindings }
}

/** The request that only moves the pin: everything else the instance has (title, settings, bindings) is sent back unchanged, since the Gateway replaces the whole instance. */
export function pinRequest(row: Pick<AppInstanceRow, 'app' | 'title' | 'settings' | 'bindings'>, pin: number | null): InstanceRequest {
  return { app: row.app, pin, title: row.title, settings: { ...row.settings }, bindings: { ...row.bindings } }
}

/** A short line of an instance's settings and bindings for the list: `portal=front, 2 bindings`. */
export function configurationLabel(row: Pick<AppInstanceRow, 'settings' | 'bindings'>): string {
  const settings = Object.entries(row.settings ?? {}).map(([k, v]) => `${k}=${v}`)
  const bindings = Object.entries(row.bindings).map(([m, i]) => `${m} → ${i}`)
  return [...settings, ...bindings].join(', ')
}

// ---- UE02: publishing a version from a folder ----

/** A file the browser read from the folder a person chose: its path as `webkitRelativePath` gives it (the folder's own name first) and its bytes. */
export interface PickedFile { path: string; bytes: Uint8Array }

/** The body of `PUT /api/v1/apps/{name}` and, without the note, of `POST /api/v1/apps/check` and of an approval of exact files. */
export interface PublishBody { manifest: string; files: Record<string, string>; note?: string }

export interface PreparedPackage {
  /** Where it came from: the folder's name, or 'the pasted manifest'. */
  source: string
  manifest: string
  files: Record<string, string>
  fileCount: number
  /** What the files add up to (the manifest not counted: the Gateway counts the files). */
  bytes: number
}

export type Prepared = { ok: true; package: PreparedPackage } | { ok: false; problem: string }

/** The most the files of one version add up to; the Gateway refuses more (AppPackage.MaximumBytes), the screen says so before sending 16 MB. */
export const MAX_PACKAGE_BYTES = 16 * 1024 * 1024

export function toBase64(bytes: Uint8Array): string {
  let text = ''
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
  return btoa(text)
}

const utf8 = (bytes: Uint8Array) => new TextDecoder('utf-8').decode(bytes)

/**
 * What a folder becomes: the same package `apps publish --dir` makes (the manifest is the `app.yaml` at the top, read as text; every other file is sent under its path inside the folder, as base64), so the hash of the files is the
 * hash the command line shows and an approval made with either stands for both. A folder with no `app.yaml` at its top is refused here, naming what to choose instead; everything else is the Gateway's to read and refuse.
 */
export function prepareFolder(picked: PickedFile[], decode: (bytes: Uint8Array) => string = utf8): Prepared {
  if (picked.length === 0) return { ok: false, problem: "Choose the folder of the built app: the one that holds app.yaml beside the files it serves." }
  const first = picked[0]!.path.split('/')[0]!
  const inside = picked.every((f) => f.path.includes('/') && f.path.split('/')[0] === first)
  const rooted = picked.map((f) => ({ path: inside ? f.path.slice(first.length + 1) : f.path, bytes: f.bytes }))
  const folder = inside ? first : 'the chosen files'
  const manifest = rooted.find((f) => f.path === 'app.yaml')
  if (!manifest) {
    const deeper = rooted.find((f) => f.path.endsWith('/app.yaml'))
    return { ok: false, problem: deeper
      ? `There is no app.yaml at the top of ${folder}. There is one in ${folder}/${deeper.path.slice(0, -'/app.yaml'.length)}: choose that folder instead.`
      : `There is no app.yaml in ${folder}: an app's folder holds its manifest at the top, beside the files it serves.` }
  }
  const files: Record<string, string> = {}
  let bytes = 0
  for (const f of rooted) {
    if (f === manifest) continue
    files[f.path] = toBase64(f.bytes)
    bytes += f.bytes.length
  }
  if (bytes > MAX_PACKAGE_BYTES) return { ok: false, problem: `The files of ${folder} add up to ${Math.ceil(bytes / (1024 * 1024))} MB; an app is at most ${MAX_PACKAGE_BYTES / (1024 * 1024)} MB. Build without source maps and sample data, or choose the build's output folder.` }
  return { ok: true, package: { source: folder, manifest: decode(manifest.bytes), files, fileCount: Object.keys(files).length, bytes } }
}

/** A manifest typed or pasted, with no files (a portal of its own mounts and home views needs none; an app that serves a page does). */
export function prepareManifest(text: string): Prepared {
  if (text.trim() === '') return { ok: false, problem: 'Paste the manifest (the text of app.yaml).' }
  return { ok: true, package: { source: 'the pasted manifest', manifest: text, files: {}, fileCount: 0, bytes: 0 } }
}

export const publishBody = (p: PreparedPackage, note: string): PublishBody => ({ manifest: p.manifest, files: p.files, ...(note.trim() === '' ? {} : { note: note.trim() }) })

/** The size as the screen says it. */
export const sizeLabel = (bytes: number) => bytes < 1024 ? `${bytes} bytes` : bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`

/** The outside origins a manifest asks for, each one a choice the approver makes: an `id` of the form `connect=https://api.example.com`, which is also what the approval's `egress` list is made of. */
export function egressChoices(about: Pick<AppAbout, 'egress'> | null | undefined): { id: string; directive: string; origin: string }[] {
  return (about?.egress ?? []).flatMap((e) => e.origins.map((origin) => ({ id: `${e.directive}=${origin}`, directive: e.directive, origin })))
}

/** The `egress` of an approval for the origins chosen, or undefined for none (an approval with no origin allows none). */
export const egressParam = (chosen: string[]): string | undefined => chosen.length === 0 ? undefined : chosen.join(',')

/** The same text with a choice turned on or off. */
export const toggled = (chosen: string[], id: string): string[] => chosen.includes(id) ? chosen.filter((c) => c !== id) : [...chosen, id]

/** Why the files cannot be published yet, or null when the call may be tried. The Gateway checks again and its refusal is shown as it says it. */
export function publishBlock(caller: AppsCaller, prepared: Prepared | null, check: AppCheck | null, checking: boolean): string | null {
  if (!caller.can_publish) return NEEDS_PUBLISHER
  if (prepared === null) return 'Choose the folder of the built app first.'
  if (!prepared.ok) return prepared.problem
  if (checking || check === null) return 'Checking these files with the Gateway…'
  if (!check.ok) return 'The Gateway found errors in these files (listed above): fix them, then choose the folder again. These are the checks of apps validate.'
  if (check.trust === 'trusted' && !check.approval_id) return `${check.name} ${check.manifest_version} asks to be trusted and no approval stands for these exact files: an approver approves them below (hash ${check.hash?.slice(0, 12)}), then they can be published.`
  return null
}

/** Why exact files cannot be approved from here, or null. The Gateway decides: the approver must hold the Approver role at the identity home and not be the author (the person signed in here). */
export function packageApproveBlock(caller: AppsCaller, check: AppCheck | null, by: string): string | null {
  if (!caller.can_approve) return NEEDS_APPROVER
  if (check === null) return 'Check the files first.'
  if (check.trust !== 'trusted') return 'Only an app that asks to be trusted needs an approval; these files are sandboxed.'
  if (check.approval_id) return `An approval already stands for these exact files (${check.approval_id}).`
  if (by.trim() === '') return 'Name the approver: a person of the identity home who holds the Approver role.'
  if (by.trim().toLowerCase() === caller.you.trim().toLowerCase()) return `${caller.you} is signed in here and would publish these files, so cannot approve them: the author and the approver are two people. Name another approver.`
  return null
}

// ---- UE03: a navigation set ----

export interface NavEntryForm { instance: string; label: string; icon: string }

/** What the navigation set form holds: the set's name (editable; changing the name of a set that exists renames it), its title and its members in the order the menu shows them. */
export interface NavForm {
  name: string
  title: string
  entries: NavEntryForm[]
  /** The name the set had when it was opened, or null for a new set. */
  original: string | null
}

/** The body of `PUT /api/v1/app-navigation/{name}`. */
export interface NavigationRequest {
  title: string
  entries: { instance: string; label: string | null; icon: string | null }[]
  rename_from?: string
}

export const emptyNavForm = (): NavForm => ({ name: '', title: '', entries: [], original: null })

export const navFormFromSet = (set: NavigationSetRow): NavForm => ({
  name: set.name,
  title: set.title,
  entries: set.entries.map((e) => ({ instance: e.instance, label: e.label ?? '', icon: e.icon ?? '' })),
  original: set.name,
})

/** The members one position up (`-1`) or down (`+1`); an end of the list stays where it is. */
export function moveEntry(entries: NavEntryForm[], index: number, delta: -1 | 1): NavEntryForm[] {
  const to = index + delta
  if (index < 0 || index >= entries.length || to < 0 || to >= entries.length) return entries
  const next = [...entries]
  next[index] = entries[to]!
  next[to] = entries[index]!
  return next
}

/** The instances that can still be added: those that exist and are not listed yet, by name. A member twice is not offered, and the store refuses it too. */
export const availableMembers = (instances: string[], entries: NavEntryForm[]): string[] =>
  instances.filter((i) => !entries.some((e) => e.instance === i)).sort((a, b) => a.localeCompare(b))

/** Whether saving renames a set that exists (the old name stops being served in the same call). */
export const isRename = (form: NavForm) => form.original !== null && form.original !== form.name.trim()

/** Why the set cannot be saved yet, or null. Members, names and the rest are the Gateway's to read and refuse; this is only what makes the call mean nothing, and the one case the Gateway would answer by quietly replacing another set. */
export function navFormProblem(form: NavForm, existing: string[]): string | null {
  const name = form.name.trim()
  if (name === '') return 'Name the set: lower-case letters, digits, - and _.'
  if (existing.includes(name) && name !== form.original) return `There is already a navigation set named ${name}; choose another name, or change that one.`
  if (form.title.trim() === '') return "Give the set a title: it is the menu's heading."
  if (form.entries.length === 0) return 'List at least one instance: a menu with no member shows nothing.'
  return null
}

export function navRequest(form: NavForm): NavigationRequest {
  return {
    title: form.title.trim(),
    entries: form.entries.map((e) => ({ instance: e.instance, label: e.label.trim() === '' ? null : e.label.trim(), icon: e.icon.trim() === '' ? null : e.icon.trim() })),
    ...(isRename(form) ? { rename_from: form.original! } : {}),
  }
}

/** The members of a set as the list shows them: `acme (Acme) → pinned`. */
export const entriesLabel = (set: Pick<NavigationSetRow, 'entries'>) => set.entries.map((e) => e.label ? `${e.instance} (${e.label})` : e.instance).join(' → ')
