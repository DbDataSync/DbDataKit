import type { ReactNode } from 'react'

/** One destination in the navigation. `inert` draws the icon with nothing behind it (a placeholder the product has not built yet). */
export interface RailItem {
  /** The route; a section stays lit while the location is anywhere inside it. Omit for an `inert` item. */
  to?: string
  title: string
  icon: ReactNode
  /** `data-testid` of the link. */
  testId?: string
  /** Shown only to an admin. The API enforces the role for real; hiding the item only avoids offering a viewer a destination that would 403 on arrival. */
  adminOnly?: boolean
  inert?: boolean
  /** A second, quieter word after the name (a server's product). */
  note?: string
  /** Set when what the entry stands for does not answer (a server): the entry is marked and the words are its tooltip. */
  problem?: string
  /** The paired server this entry opens: while that server's screens are shown, its own entries are nested under this one. */
  serverId?: string
  /** Other routes that are pages under this entry (reached from it, with no entry of their own): the entry stays lit on them too. */
  pages?: string[]
}

/** A named group of entries. A group with no `title` is drawn without a heading (Home before the groups, Docs after them). */
export interface NavGroup {
  title?: string
  /** `data-testid` of the group; `nav-group-<name>` when omitted and the group has a title. */
  testId?: string
  items: RailItem[]
}

/** The navigation as a product gives it: a flat list of entries (one untitled group) or named groups. */
export type Nav = RailItem[] | NavGroup[]

function isGroup(entry: RailItem | NavGroup): entry is NavGroup {
  return Array.isArray((entry as NavGroup).items)
}

/** The navigation as groups: a flat list is one untitled group, so a product that gives a plain rail is drawn as it was. */
export function toGroups(nav: Nav): NavGroup[] {
  if (nav.length === 0) return []
  return isGroup(nav[0]) ? (nav as NavGroup[]) : [{ items: nav as RailItem[] }]
}

/** The test id of a group: its own, or `nav-group-` and its title in lower case with spaces as dashes (`nav-group-build-and-release`). */
export function groupTestId(group: NavGroup): string | undefined {
  return group.testId ?? (group.title ? `nav-group-${group.title.toLowerCase().replace(/[^a-z0-9]+/g, '-')}` : undefined)
}

/**
 * The groups a person sees: an administrators' entry is left out for a viewer, and a group left with no entry is not drawn at all, so a viewer never
 * reads a heading with nothing under it.
 */
export function visibleGroups(groups: NavGroup[], isAdmin: boolean): NavGroup[] {
  return groups
    .map((group) => ({ ...group, items: group.items.filter((item) => !item.adminOnly || isAdmin) }))
    .filter((group) => group.items.length > 0)
}

function under(pathname: string, route: string): boolean {
  if (route === '/') return pathname === '/'
  const bare = route.replace(/\/+$/, '')
  return pathname === bare || pathname.startsWith(bare + '/')
}

/**
 * Whether an entry is lit for this location (the path inside the product): the location is its route or anywhere inside it, or one of the pages under
 * it. The root route is lit only on itself, or Home would be lit everywhere.
 */
export function isEntryActive(pathname: string, item: RailItem): boolean {
  if (!item.to) return false
  return under(pathname, item.to) || (item.pages ?? []).some((page) => under(pathname, page))
}

/** Below this width the navigation starts as the icon rail, so a dashboard on a small screen keeps its width. */
export const NAV_COLLAPSE_BELOW = 1000

/** Where the browser keeps whether the person collapsed the navigation. */
export const NAV_COLLAPSED_KEY = 'dbdatakit.nav.collapsed'

/**
 * Whether the navigation starts collapsed: always in a window narrower than 1000 pixels; otherwise as the person last left it (`'true'` or `'false'` in
 * the browser), and open when they never chose.
 */
export function startsCollapsed(stored: string | null, width: number): boolean {
  if (width < NAV_COLLAPSE_BELOW) return true
  return stored === 'true'
}
