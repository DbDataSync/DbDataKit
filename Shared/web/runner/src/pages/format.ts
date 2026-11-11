/** A time the runner sent (UTC, ISO 8601) in the viewer's own zone; empty for none. */
export function when(iso: string | null | undefined): string {
  if (!iso) return ''
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString()
}

/** How long a run took, from its start and end. */
export function took(started: string, finished: string | null): string {
  if (!finished) return 'running'
  const ms = Date.parse(finished) - Date.parse(started)
  if (!Number.isFinite(ms) || ms < 0) return ''
  return ms < 1000 ? `${ms} ms` : ms < 60_000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms / 60_000)} min`
}
