import type { LinkInfo, LinkScalar } from '../views/links'
import { linkQuery } from '../views/links'

/**
 * The pure half of the shell's dialog and side panel for link targets (plan 32 section 9f, DBR36). A link whose `as` is `dialog` or `panel` opens its target in a frame of its own, inside the page, instead of navigating:
 * the target's address carries the link's values in its query string (the same `linkQuery` as a navigating link), and the names of the fields the link `lock`s, which the target's form shows read-only (the server still
 * refuses a forged value: a lock is the form's courtesy, never the authority). The frame speaks back with two messages only, read strictly.
 */

/** The query parameter a target reads its locked fields from: `_lock=status,customer_id` (names, not values). */
export const LOCK_PARAMETER = '_lock'

/** The address a target is opened at: the host's route for it, the values the link gives for the row, and the locked names. A route that already has a query keeps it. */
export function surfaceAddress(route: string, values: Record<string, LinkScalar | LinkScalar[]>, lock: readonly string[] = []): string {
  const query = [linkQuery(values), lock.length ? `${LOCK_PARAMETER}=${lock.map(encodeURIComponent).join(',')}` : ''].filter((q) => q.length > 0).join('&')
  if (query === '') return route
  return route + (route.includes('?') ? '&' : '?') + query
}

/** What a target frame may say: that it saved (the form's write was accepted) or that it wants to close. */
export type FromTarget = { type: 'views:saved' } | { type: 'views:close' }

export function readFromTarget(data: unknown): FromTarget | null {
  if (typeof data !== 'object' || data === null) return null
  const type = (data as { type?: unknown }).type
  return type === 'views:saved' || type === 'views:close' ? { type } : null
}

/** The widgets that resolve again when a dialog or panel closes: after a save, a link's `then` says which (`refresh`: the widget the click came from; a list: those; `none`: nothing); a close without a save never refreshes. */
export function thenWidgets(link: Pick<LinkInfo, 'thenKind' | 'thenWidgets'>, source: string, saved: boolean): string[] {
  if (!saved) return []
  if (link.thenKind === 'refresh') return [source]
  if (link.thenKind === 'widgets') return [...link.thenWidgets]
  return []
}
