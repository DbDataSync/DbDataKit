import type { DefinitionDetail, SlotRequirement } from './api'

/** One line of a channel's connections: a slot the manifests declare (or a binding the channel still holds for a slot no version declares now) and what it is bound to. */
export interface SlotRow {
  slot: string
  /** The kind of connection the slot asks for; null for a binding with no declaring version. */
  kind: string | null
  tasks: string[]
  versions: number[]
  boundTo: string | null
  /** The slot's access as the manifests declare it; a binding with no declaring version is write. */
  access: 'read' | 'write'
  /** The connection production binds, when this channel binds nothing and the slot is a read slot: what it reads (inherited, plan 21 CH03); null otherwise. */
  inheritedFrom: string | null
  /** The revision of the binding, which the runner checks when one slot is changed. */
  revision: number | null
  declared: boolean
}

/** What the person typed, by slot: only the slots they touched. */
export type BindingEdits = Record<string, string>

/** Production's binding of a read slot this channel leaves unbound (plan 21 CH03); null for a write slot, for production itself, and when production binds nothing either. */
function inheritedRead(detail: Pick<DefinitionDetail, 'bindings'>, channel: string, s: SlotRequirement): string | null {
  if (s.access !== 'read' || channel === 'prod' || detail.bindings.some((b) => b.channel === channel && b.slot === s.slot)) return null
  return detail.bindings.find((b) => b.channel === 'prod' && b.slot === s.slot)?.bound_to ?? null
}

/** The slots of a channel: every slot a served or staged version declares, with what it is bound to, then the bindings no version declares (kept, shown last). */
export function slotRows(detail: Pick<DefinitionDetail, 'slots' | 'bindings'>, channel: string): SlotRow[] {
  const declared: SlotRequirement[] = (detail.slots ?? []).filter((s) => s.channel === channel)
  const bound = new Map(detail.bindings.filter((b) => b.channel === channel).map((b) => [b.slot, b]))
  const rows: SlotRow[] = declared.map((s) => ({ slot: s.slot, kind: s.kind, tasks: s.tasks, versions: s.versions, boundTo: bound.get(s.slot)?.bound_to ?? null, access: s.access ?? 'write', inheritedFrom: inheritedRead(detail, channel, s), revision: bound.get(s.slot)?.revision ?? null, declared: true }))
  const known = new Set(declared.map((s) => s.slot))
  for (const [slot, b] of [...bound].sort(([a], [z]) => a.localeCompare(z))) {
    if (!known.has(slot)) rows.push({ slot, kind: null, tasks: [], versions: [], boundTo: b.bound_to, access: 'write', inheritedFrom: null, revision: b.revision, declared: false })
  }
  return rows
}

/** The declared slots with nothing bound: a version that declares one cannot run in the channel until it is bound. */
export const unboundSlots = (rows: SlotRow[]) => rows.filter((r) => r.declared && r.boundTo === null && r.inheritedFrom === null).map((r) => r.slot)

/** The slots whose typed value differs from what is bound now (text is trimmed; a slot typed back to its current value is not a change). */
export function changedSlots(rows: SlotRow[], edits: BindingEdits): { slot: string; value: string; revision: number | null }[] {
  return rows.flatMap((r) => {
    if (!(r.slot in edits)) return []
    const value = edits[r.slot].trim()
    return value === (r.boundTo ?? '') ? [] : [{ slot: r.slot, value, revision: r.revision }]
  })
}

/** What is wrong with a typed connection, in words; null when it can be sent. The runner checks again; a connection is a name or a reference, never a login. */
export function bindingProblem(slot: string, value: string): string | null {
  const text = value.trim()
  if (text === '') return `Name the connection for ${slot}; a slot cannot be unbound here.`
  if (/\s/.test(text)) return `The connection for ${slot} is a name or a reference without spaces.`
  if (text.length > 200) return `The connection for ${slot} is longer than 200 characters.`
  return null
}

/** Why Save cannot be pressed, in words; null when it can. */
export function bindingBlock(canEdit: boolean, rows: SlotRow[], edits: BindingEdits): string | null {
  if (!canEdit) return 'Binding connections needs the admin role on this server.'
  const changes = changedSlots(rows, edits)
  if (changes.length === 0) return 'Change a connection first.'
  for (const c of changes) {
    const problem = bindingProblem(c.slot, c.value)
    if (problem) return problem
  }
  return null
}

/**
 * The body of the bind call: the slots that changed. With exactly one changed slot that already has a binding its revision is sent, so a change made by someone else meanwhile is refused instead of overwritten (the runner takes one revision
 * for the call, so a call that changes several slots does not carry one). Null when nothing can be sent.
 */
export function bindingsRequest(rows: SlotRow[], edits: BindingEdits): { slots: Record<string, string>; expected_revision?: number } | null {
  const changes = changedSlots(rows, edits)
  if (changes.length === 0 || changes.some((c) => bindingProblem(c.slot, c.value) !== null)) return null
  const slots = Object.fromEntries(changes.map((c) => [c.slot, c.value]))
  return changes.length === 1 && changes[0].revision !== null ? { slots, expected_revision: changes[0].revision } : { slots }
}

/** The connection names the runner knows (those its channels bind), to offer when typing; each once, in order. */
export const connectionNames = (connections: { name: string }[] | undefined) => [...new Set((connections ?? []).map((c) => c.name))].sort((a, b) => a.localeCompare(b))

/** What the require-signatures switch says and what turning it on does while signature checking is not built (every publish is then refused). */
export function signaturesText(on: boolean): string {
  return on
    ? 'On: a version is accepted only with a valid signature. Signature checking is not built yet (plan 30), so every new publish is refused until it is turned off.'
    : 'Off: versions are accepted unsigned and recorded as unsigned.'
}
