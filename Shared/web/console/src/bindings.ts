import { request } from '@dbdatakit/shell'
import type { Diagnostic } from './agents'

/*
 * Bindings (plan 27a IX23, docs/channel-bindings.md): ONE shape for every name the suite binds to a connection per release channel. A slot has a kind (a chat model, an embeddings model,
 * a delivery channel, or a runner package's connection slot), and per release channel an ordered list of choices (a connection and, for a model, which of its models). The console keeps the
 * first three in bindings.yml; the runner keeps its own (package/1 is frozen) and the same screen reads them from it.
 */

export type SlotKind = 'model-chat' | 'model-embeddings' | 'delivery' | 'runner'

export interface BindingEntry { connection: string; model: string | null; usable: boolean; problem: string | null; kind: string | null; target: string | null }
export interface BindingRow { release: string; wildcard: boolean; entries: BindingEntry[]; needsConfirmation: boolean; confirmed: boolean; effect: string | null }
export interface BindingSlot {
  section: string; kind: SlotKind; name: string; usedBy: string[]; limitPerMinute: number | null; rows: BindingRow[]
  /** Set on a runner's slot: the package it belongs to (the runner binds per package and release channel). */
  runner?: { server: string; package: string; kindOfConnection: string }
}
export interface BindingConnection { name: string; kind: string; purpose: string | null; models: string[]; canSend: boolean; target: string | null; cloud: boolean | null }
export interface BindingsView {
  file: string; yaml: string; diagnostics: Diagnostic[]; earlier: string | null; releaseChannels: string[]; connections: BindingConnection[]; slots: BindingSlot[]; postureSetting: string; postureEffect: string
}
export interface Unconfirmed { scope: string; section: string; slot: string; release: string; connection: string; effect: string }

/** The choices of one slot as a screen edits them: per release channel an ordered list. */
export type SlotDraft = Record<string, { connection: string; model: string }[]>

export const KIND_LABEL: Record<SlotKind, string> = { 'model-chat': 'Chat model', 'model-embeddings': 'Embedding model', delivery: 'Delivery channel', runner: 'Runner slot' }

/** Whether a slot of this kind names a model as well as a connection. */
export const needsModel = (kind: SlotKind): boolean => kind === 'model-chat' || kind === 'model-embeddings'

/** How many choices a release channel of this kind may hold: a chat model has a fallback list, the rest one. */
export const maxChoices = (kind: SlotKind): number => (kind === 'model-chat' ? 8 : 1)

/** A slot or release channel name the reader accepts (`*` is a release channel for every release channel without its own). */
export const isSlotName = (name: string): boolean => /^[A-Za-z0-9_-]{1,64}$/.test(name)
export const isReleaseName = (name: string): boolean => name === '*' || isSlotName(name)

/** The release channels of a slot in the order a person reads them: prod first, `*` last. */
export const orderedReleases = (releases: string[]): string[] => [...releases].sort((a, b) => rank(a) - rank(b) || a.localeCompare(b))
const rank = (r: string) => (r === 'prod' ? 0 : r === '*' ? 2 : 1)

/** The draft of a slot's saved rows. */
export const draftOf = (slot: BindingSlot): SlotDraft =>
  Object.fromEntries(slot.rows.map((r) => [r.release, r.entries.map((e) => ({ connection: e.connection, model: e.model ?? '' }))]))

export const sameDraft = (a: SlotDraft, b: SlotDraft): boolean => JSON.stringify(canonical(a)) === JSON.stringify(canonical(b))
const canonical = (d: SlotDraft) => Object.keys(d).sort().map((k) => [k, d[k]])

/** What is wrong with a draft before it is sent (the server checks again and says more: which connection exists and fits). */
export function draftProblem(kind: SlotKind, draft: SlotDraft): string | null {
  for (const [release, list] of Object.entries(draft)) {
    if (!isReleaseName(release)) return `\`${release}\` is not a release channel name: letters, digits, dashes and underscores (or \`*\` for every release channel).`
    if (list.length === 0) continue
    if (list.length > maxChoices(kind)) return `${KIND_LABEL[kind]} ${release === '*' ? 'for every release channel' : `in ${release}`} takes ${maxChoices(kind) === 1 ? 'one connection' : `at most ${maxChoices(kind)} choices`}.`
    for (const c of list) {
      if (c.connection === '') return `Choose a connection for ${release}.`
      if (needsModel(kind) && c.model === '') return `Choose a model of ${c.connection} for ${release}.`
    }
  }
  return null
}

/** The body of the save of a slot: empty release channels are left out, and a model is sent only for a model slot. */
export function slotBody(kind: SlotKind, draft: SlotDraft, confirm: string[] = [], note?: string): { channels: Record<string, { connection: string; model?: string }[]>; confirm?: string[]; note?: string } {
  const channels: Record<string, { connection: string; model?: string }[]> = {}
  for (const [release, list] of Object.entries(draft)) if (list.length > 0) channels[release] = list.map((c) => (needsModel(kind) ? { connection: c.connection, model: c.model } : { connection: c.connection }))
  return { channels, ...(confirm.length > 0 ? { confirm } : {}), ...(note ? { note } : {}) }
}

/** Moves one choice of a list one place up or down: the fallback order. Out of range changes nothing. */
export function moveChoice<T>(list: T[], index: number, by: -1 | 1): T[] {
  const to = index + by
  if (index < 0 || index >= list.length || to < 0 || to >= list.length) return list
  const next = [...list]
  ;[next[index], next[to]] = [next[to], next[index]]
  return next
}

/** The connections a kind of slot can take, for the choice: a delivery channel the ones that send, a model slot the providers of its purpose. */
export function connectionsFor(kind: SlotKind, all: BindingConnection[]): BindingConnection[] {
  if (kind === 'delivery') return all.filter((c) => c.canSend)
  if (kind === 'model-chat') return all.filter((c) => c.kind === 'model-provider' && (c.purpose ?? 'chat') === 'chat')
  if (kind === 'model-embeddings') return all.filter((c) => c.kind === 'model-provider' && c.purpose === 'embeddings')
  return all
}

/** The scope of the recorded production-in-non-production choice (IX17), as the server names it. */
export const scopeOf = (section: string, slot: string, release: string): string => `bindings/${section}/${slot}/${release}`

/**
 * The choices a draft makes that need the administrator's confirmation (IX17): a non-production release channel of a slot that sends or writes (a delivery channel, a runner slot) that uses a connection production uses.
 * The server decides again; this is what the screen asks before it sends.
 */
export function needsConfirmation(slot: Pick<BindingSlot, 'kind' | 'section' | 'name'>, draft: SlotDraft): { scope: string; release: string; connection: string }[] {
  if (slot.kind !== 'delivery' && slot.kind !== 'runner') return []
  const prod = (draft.prod?.length ? draft.prod : draft['*'] ?? []).map((c) => c.connection)
  if (prod.length === 0) return []
  const out: { scope: string; release: string; connection: string }[] = []
  for (const [release, list] of Object.entries(draft)) {
    if (release === 'prod') continue
    const shared = list.find((c) => prod.includes(c.connection))
    if (shared) out.push({ scope: scopeOf(slot.section, slot.name, release), release, connection: shared.connection })
  }
  return out
}

/** One row in a line: what each release channel uses, the primary first and the fallback after it. */
export const rowLine = (r: BindingRow): string => r.entries.map((e, i) => `${i === 0 ? '' : 'then '}${e.connection}${e.model ? `/${e.model}` : ''}${e.usable ? '' : ' (cannot be used)'}`).join(' ')

/** Why a slot has no row for a release channel, in the words the server uses: it is unbound and never takes another's. */
export const unboundWords = (slot: Pick<BindingSlot, 'name' | 'kind' | 'rows'>, release: string): string => {
  const bound = slot.rows.map((r) => r.release).filter((r) => r !== '*')
  const where = bound.length === 0 ? 'bound nowhere' : `bound in ${bound.join(', ')}`
  return `${KIND_LABEL[slot.kind]} '${slot.name}' is not bound in ${release} (${where}); it never uses another release channel's connection.`
}

// ---- the runner's slots, read from the runner in the same shape ----

export interface RunnerDefinitions { definitions: { name: string }[] }
export interface RunnerDetail {
  slots: { channel: string; slot: string; kind: string; tasks: string[]; versions: number[] }[]
  bindings: { channel: string; slot: string; bound_to: string; revision: number }[]
}

/**
 * A runner package's connection slots as binding slots: the runner binds a slot per package and release channel (its channel) to one connection (a name or a reference), never `*` and never inherited, which is the suite's rule already; this only shows it in the same shape.
 * The slot is named `package/slot`.
 */
export function runnerSlots(server: string, packageName: string, detail: RunnerDetail): BindingSlot[] {
  const bySlot = new Map<string, BindingSlot>()
  for (const s of detail.slots) {
    const key = s.slot
    let slot = bySlot.get(key)
    if (!slot) { slot = { section: 'runner', kind: 'runner', name: `${packageName}/${s.slot}`, usedBy: s.tasks.map((t) => `${packageName}/${t}`), limitPerMinute: null, rows: [], runner: { server, package: packageName, kindOfConnection: s.kind } }; bySlot.set(key, slot) }
    const bound = detail.bindings.find((b) => b.channel === s.channel && b.slot === s.slot)
    slot.rows.push({ release: s.channel, wildcard: false, entries: bound ? [{ connection: bound.bound_to, model: null, usable: true, problem: null, kind: s.kind, target: null }] : [], needsConfirmation: false, confirmed: false, effect: null })
  }
  return [...bySlot.values()]
}

export interface RunnerTestAnswer { slot: string; bound_to: string | null; ok: boolean; step: string; message: string; connected: boolean }

const RUNNER = '/relay/console-runner/api/v1'
export const runnerApi = {
  definitions: () => request<RunnerDefinitions>(`${RUNNER}/definitions/integration`),
  detail: (name: string) => request<RunnerDetail>(`${RUNNER}/definitions/integration/${encodeURIComponent(name)}`),
  test: (channel: string, name: string, slot: string) => request<RunnerTestAnswer>(`${RUNNER}/bindings/${encodeURIComponent(channel)}/integration/${encodeURIComponent(name)}/test`, json('POST', { slot, role: 'default' })),
}

/** Every package slot of the runner, as binding slots; a runner that does not answer gives none (the screen says it could not be read). */
export async function loadRunnerSlots(server = 'console-runner'): Promise<BindingSlot[]> {
  const list = await runnerApi.definitions()
  const out: BindingSlot[] = []
  for (const d of list.definitions) out.push(...runnerSlots(server, d.name, await runnerApi.detail(d.name)))
  return out
}

// ---- the console's bindings ----

const json = (method: string, body: unknown): RequestInit => ({ method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })

export interface SaveAnswer { saved: boolean; diagnostics: Diagnostic[]; view?: BindingsView; needsConfirmation?: Unconfirmed[] }

export const bindingsApi = {
  get: () => request<BindingsView>('/api/bindings'),
  setSlot: (section: string, name: string, body: ReturnType<typeof slotBody>) => request<SaveAnswer>(`/api/bindings/slots/${section}/${encodeURIComponent(name)}`, json('PUT', body)),
  removeSlot: (section: string, name: string) => request<SaveAnswer>(`/api/bindings/slots/${section}/${encodeURIComponent(name)}`, { method: 'DELETE' }),
  check: (yaml: string) => request<{ diagnostics: Diagnostic[]; needsConfirmation: Unconfirmed[] }>('/api/bindings/check', json('POST', { yaml })),
  save: (yaml: string, confirm: string[] = [], note?: string) => request<SaveAnswer>('/api/bindings', json('PUT', { yaml, confirm, note })),
}
