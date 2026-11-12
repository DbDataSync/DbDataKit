import type { CacheEvents, CacheSettingRow } from './cacheTypes'

/** The words of the cache screen (plan 24a UC16); no view logic, so a test can read them. */

/** A rate from 0 to 1 as a percent, or a dash when nothing has been asked yet. */
export function percent(rate: number | null | undefined): string {
  return rate === null || rate === undefined ? '–' : `${Math.round(rate * 100)} %`
}

export function bytesText(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/** A time-to-live: `90 s`, `5 min`, `2 h`. */
export function ttlText(seconds: number): string {
  if (seconds < 120) return `${seconds} s`
  if (seconds < 7200) return `${Math.round(seconds / 60)} min`
  return `${Math.round(seconds / 3600)} h`
}

/** What the setting of a definition does: off, on, or on and not used (with the reason the Gateway gives). */
export function settingState(row: Pick<CacheSettingRow, 'enabled' | 'active' | 'note'>): { label: string; tone: 'off' | 'on' | 'warn' } {
  if (!row.enabled) return { label: 'Off', tone: 'off' }
  if (row.active) return { label: 'On', tone: 'on' }
  return { label: `On, not used: ${row.note ?? 'the Gateway does not cache it'}`, tone: 'warn' }
}

/** Who shares an entry, in words (the three scopes of the `cache` section). */
export function scopeText(scope: string): string {
  switch (scope) {
    case 'caller': return 'each caller on their own'
    case 'rules': return 'callers with the same directory attributes'
    case 'shared': return 'everyone'
    default: return scope
  }
}

export function ago(then: Date, now: Date): string {
  const seconds = Math.max(0, Math.round((now.getTime() - then.getTime()) / 1000))
  if (seconds < 90) return `${seconds} s ago`
  if (seconds < 5400) return `${Math.round(seconds / 60)} min ago`
  return seconds < 172800 ? `${Math.round(seconds / 3600)} h ago` : `${Math.round(seconds / 86400)} days ago`
}

/** What the cache has heard of data changes, so a stale entry is not a silent surprise. `now` is a parameter so a test can fix the clock. */
export function eventsText(events: CacheEvents, now: Date = new Date()): string {
  if (events.received === 0) return 'No data change has been reported to this instance. Entries leave when their time-to-live ends, when a definition is published, or when an administrator clears them.'
  const last = events.last_at ? ago(new Date(events.last_at), now) : 'at an unknown time'
  return `${events.received} data ${events.received === 1 ? 'change' : 'changes'} reported, the last ${last} by ${events.last_product ?? 'a producer'} for ${events.last_dataset ?? 'a dataset'}; ${events.cleared} ${events.cleared === 1 ? 'entry' : 'entries'} cleared in all.`
}
