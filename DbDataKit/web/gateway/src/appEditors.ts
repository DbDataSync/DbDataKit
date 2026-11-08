import type { AppDetail, AppInstanceRow } from './types'

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
