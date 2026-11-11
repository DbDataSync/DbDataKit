import type { ReplicationSetPublication, RunnerPackageView } from '../api/types'

/** One slot the publish would declare, and the connection of the runner it should be bound to unless the person says otherwise. */
export interface SlotChoice {
  slot: string
  /** The Sync connection(s) the slot is for. The runner's connection bound to it carries the same name (the login is injected under it). */
  connections: string[]
  /** What the runner has it bound to in the channel now, or null. */
  boundTo: string | null
  /** What goes in the box: the person's edit, else the binding the runner has, else the Sync connection's own name. */
  value: string
}

/** The slots a publish declares (from the check), with what the runner has them bound to (from the package) and the person's edits. */
export function slotChoices(check: ReplicationSetPublication | undefined, view: RunnerPackageView | undefined, edits: Record<string, string>): SlotChoice[] {
  const wanted = new Map<string, Set<string>>()
  for (const r of check?.replications ?? [])
    for (const slot of r.slots) {
      const found = wanted.get(slot) ?? new Set<string>()
      const name = r.slotConnections?.[slot]
      if (name) found.add(name)
      wanted.set(slot, found)
    }
  for (const s of view?.slots ?? []) if (!wanted.has(s.slot)) wanted.set(s.slot, new Set())
  return [...wanted.entries()].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)).map(([slot, names]) => {
    const boundTo = view?.slots.find((s) => s.slot === slot)?.boundTo ?? null
    const connections = [...names].sort()
    return { slot, connections, boundTo, value: edits[slot] ?? boundTo ?? connections[0] ?? '' }
  })
}

/** What `bind` is sent: every slot that has something in its box and is not bound to exactly that already. */
export function bindingsToSend(choices: SlotChoice[]): Record<string, string> {
  const send: Record<string, string> = {}
  for (const c of choices) if (c.value.trim() !== '' && c.value.trim() !== c.boundTo) send[c.slot] = c.value.trim()
  return send
}

/** One line on where the package stands on the runner, for the badge at the top of the screen. */
export function packageState(view: RunnerPackageView | undefined): { key: 'unlinked' | 'unpublished' | 'serving' | 'staged' | 'failing'; text: string } {
  if (!view?.linked) return { key: 'unlinked', text: 'No runner is set' }
  if (!view.published) return { key: 'unpublished', text: 'Not published to the runner yet' }
  if (view.staged != null) {
    if (view.staging && view.staging.failed.length > 0) return { key: 'failing', text: `Version ${view.staged} is staged and a runner failed to prepare it` }
    if (view.staging?.allReady) return { key: 'staged', text: `Version ${view.staged} is staged and ready on every runner` }
    return { key: 'staged', text: `Version ${view.staged} is staged: ${view.staging?.summary ?? 'the runners are preparing it'}` }
  }
  return view.active != null ? { key: 'serving', text: `Version ${view.active} is served in ${view.channel}` } : { key: 'unpublished', text: `Version ${view.latest ?? '?'} is published but none is served in ${view.channel}` }
}

/** A schedule as the column shows it. */
export function scheduleText(r: { mode: string; cron: string | null; frequencySeconds: number | null }): string {
  return r.mode === 'Continuous' ? `continuous, every ${r.frequencySeconds ?? '?'} s` : `periodic, ${r.cron ?? '?'}`
}
