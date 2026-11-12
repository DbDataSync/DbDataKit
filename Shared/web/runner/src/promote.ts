// The Promote action of the Packages screen (plan 30 PB06), hosted by the console only: a version of a package goes from this runner's group to another one, as the signed-in person.
import { request } from '@dbdatakit/shell'

/** What the console's `POST /api/promotions` takes. The target channel's own bindings are slot to the name of what it is bound to there, never a value. */
export interface PromoteBody {
  from: string
  to: string
  name: string
  fromChannel: string
  toChannel: string
  version?: number
  mode?: 'serve' | 'stage'
  bindings?: Record<string, string>
  note?: string
}

/** What a promotion answered: the version it read here, the version the target holds now (each group numbers its own), whether the target changed, and how many files were sent and how many it already had. */
export interface PromoteAnswer {
  kind: string
  name: string
  fromChannel: string
  toChannel: string
  sourceVersion: number
  targetVersion: number
  changed: boolean
  mode: string
  signature: string
  sent: number
  skipped: number
  bound: string[]
}

/** A server of the console as `GET /api/instances` lists it (only what the action needs). */
export interface ConsoleServer { id: string; name: string; product: string | null; reachable: boolean }

export const promoteApi = {
  servers: () => request<ConsoleServer[]>('/api/instances'),
  promote: (body: PromoteBody) => request<PromoteAnswer>('/api/promotions', { method: 'POST', body: JSON.stringify(body) }),
}

/** The id of the server the console relays to, from the module's API base (`/relay/<id>`); null when the screens are not hosted by a console (a runner's own app has no promotions). */
export function hostedServer(apiBase: string): string | null {
  const m = /^\/relay\/([^/]+)\/?$/.exec(apiBase)
  return m ? decodeURIComponent(m[1]!) : null
}

/** The runners a package can go to: every other runner the console manages that answers. */
export function targetServers(all: ConsoleServer[], here: string): ConsoleServer[] {
  return all.filter((s) => s.product === 'dbdatarunner' && s.id !== here && s.reachable)
}

export interface PromoteForm {
  to: string
  fromChannel: string
  toChannel: string
  /** The version to send as text; empty is the one the channel serves. */
  version: string
  /** `''` is the kind's default in the target group. */
  mode: '' | 'serve' | 'stage'
  /** One `slot=connection` per line: the target channel's own bindings. */
  bindings: string
}

export const emptyPromoteForm: PromoteForm = { to: '', fromChannel: '', toChannel: '', version: '', mode: '', bindings: '' }

/** The lines `slot=connection` as a map; a line that is not one is named (a binding is a name, never a value, so there is no other form). */
export function parseBindings(text: string): { ok: true; slots: Record<string, string> } | { ok: false; problem: string } {
  const slots: Record<string, string> = {}
  for (const raw of text.split('\n')) {
    const line = raw.trim()
    if (line === '') continue
    const at = line.indexOf('=')
    const slot = at > 0 ? line.slice(0, at).trim() : ''
    const bound = at > 0 ? line.slice(at + 1).trim() : ''
    if (slot === '' || bound === '') return { ok: false, problem: `"${line}" is not slot=connection.` }
    slots[slot] = bound
  }
  return { ok: true, slots }
}

/** Why the form cannot be sent yet, or null. */
export function promoteBlock(form: PromoteForm): string | null {
  if (form.to === '') return 'Choose the runner it goes to.'
  if (form.fromChannel.trim() === '') return 'Name the channel it is read from.'
  if (form.toChannel.trim() === '') return 'Name the channel it goes to.'
  if (form.version.trim() !== '' && !/^[1-9][0-9]*$/.test(form.version.trim())) return 'A version is a whole number.'
  const bindings = parseBindings(form.bindings)
  return bindings.ok ? null : bindings.problem
}

/** The request for a form that has no block. */
export function promoteBody(here: string, name: string, form: PromoteForm): PromoteBody {
  const bindings = parseBindings(form.bindings)
  const slots = bindings.ok ? bindings.slots : {}
  return {
    from: here, to: form.to, name, fromChannel: form.fromChannel.trim(), toChannel: form.toChannel.trim(),
    ...(form.version.trim() === '' ? {} : { version: Number(form.version.trim()) }),
    ...(form.mode === '' ? {} : { mode: form.mode }),
    ...(Object.keys(slots).length === 0 ? {} : { bindings: slots }),
  }
}

/** What a promotion did, in words. */
export function promotionText(a: PromoteAnswer, target: string): string {
  const files = a.sent === 0 ? 'No file was sent (the target had them all)' : `${a.sent} file${a.sent === 1 ? '' : 's'} sent, ${a.skipped} already there`
  const result = a.changed ? `${target} now holds version ${a.targetVersion} in ${a.toChannel} (${a.mode})` : `${target} already held this content: version ${a.targetVersion} in ${a.toChannel}, nothing changed`
  return `Version ${a.sourceVersion} of ${a.name}: ${result}. ${files}.`
}
