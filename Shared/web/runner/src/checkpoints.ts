import type { CheckpointChange, CheckpointItem } from './api'

/** A checkpoint's parts as one line: `modified=2026-11-04T09:15:00Z, last_id=1042377`. */
export function partsLine(item: CheckpointItem): string {
  const parts = Object.entries(item.parts).sort(([a], [b]) => a.localeCompare(b))
  return parts.length === 0 ? '(no value)' : parts.map(([name, part]) => `${name}=${part.value}`).join(', ')
}

/** What a key is called on screen: the empty key is the checkpoint as a whole. */
export const keyLabel = (key: string) => (key === '' ? 'the whole task' : key)

/** One change as a sentence: `at 10 -> 20 by run 3f2a9c1d`, `at set to 10 by ...`, `at (was 20) removed by ...`. */
export function describeChange(change: CheckpointChange): string {
  const by = change.run_id ? `run ${change.run_id.slice(0, 8)}` : change.actor ?? 'someone'
  if (change.old === null && change.new !== null) return `${change.part} set to ${change.new} by ${by}`
  if (change.old !== null && change.new === null) return `${change.part} (was ${change.old}) removed by ${by}`
  return `${change.part} ${change.old} -> ${change.new} by ${by}`
}

/**
 * The places an operator can go back to: one per version, newest first. Rewinding to the id of a version's oldest change undoes that version and every later one, so the label says what the parts that version changed
 * were before it.
 */
export function rewindOptions(changes: CheckpointChange[]): { id: number; version: number; label: string }[] {
  const seen = new Set<number>()
  const options: { id: number; version: number; label: string }[] = []
  for (const change of changes) {
    if (seen.has(change.version)) continue
    seen.add(change.version)
    const same = changes.filter((c) => c.version === change.version)
    const oldest = same.reduce((a, b) => (a.id < b.id ? a : b))
    options.push({ id: oldest.id, version: change.version, label: `Go back before version ${change.version}: ${same.map((c) => `${c.part}=${c.old ?? '(none)'}`).join(', ')}` })
  }

  return options
}
