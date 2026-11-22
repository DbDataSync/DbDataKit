import type { JoinTokenRequest } from './api'

/** The servers that join the console with a token, as the console names them, and the program each runs `pair` with. */
export const joinProducts = [
  { id: 'runner', label: 'Runner', program: 'dbdatarunner' },
  { id: 'gateway', label: 'Gateway', program: 'dbdatagateway' },
  { id: 'workspace', label: 'Workspace', program: 'dbdataworkspace' },
] as const

/** The roles a token's ceiling can hold: the most the console may grant itself on the server that joins. */
export const joinRoles = ['viewer', 'operator', 'author', 'approver', 'admin', 'publisher'] as const

/** The longest a token works, in minutes (24 hours), and how long it works when the person does not say. */
export const MAX_MINUTES = 1440
export const DEFAULT_MINUTES = 15

/** The Add a server form as the person fills it in: every field as typed. */
export interface InviteForm {
  product: string
  roles: string[]
  group: string
  minutes: string
  /** The console's address as the server's machine reaches it. */
  url: string
}

/** The form as it opens: no server chosen, the admin ceiling (as `setup invite` makes it), no group, 15 minutes, the address this browser reached the console at. */
export function emptyInviteForm(origin: string): InviteForm {
  return { product: '', roles: ['admin'], group: '', minutes: String(DEFAULT_MINUTES), url: origin }
}

const groupPattern = /^[A-Za-z0-9._-]{1,64}$/

/** What is wrong with the form, field by field, in the words shown beside it; empty when the token can be made. */
export function inviteProblems(form: InviteForm): Partial<Record<keyof InviteForm, string>> {
  const problems: Partial<Record<keyof InviteForm, string>> = {}
  if (!joinProducts.some((p) => p.id === form.product)) problems.product = 'Choose the server that joins.'
  if (form.roles.length === 0) problems.roles = 'Choose at least one role.'
  const group = form.group.trim()
  if (group && !groupPattern.test(group)) problems.group = 'A group is letters, digits and . _ -, up to 64.'
  const minutes = form.minutes.trim()
  if (!/^\d+$/.test(minutes) || Number(minutes) < 1 || Number(minutes) > MAX_MINUTES) problems.minutes = `A whole number of minutes from 1 to ${MAX_MINUTES}.`
  if (!isConsoleAddress(form.url)) problems.url = 'An https address with a host and a port, such as https://console.example.com:7800.'
  return problems
}

/** Whether the text is an address a server can be told to join at: https, a host and a port at most, nothing after it. */
export function isConsoleAddress(text: string): boolean {
  let url: URL
  try { url = new URL(text.trim()) } catch { return false }
  return url.protocol === 'https:' && url.hostname !== '' && (url.pathname === '/' || url.pathname === '') && !url.search && !url.hash && !url.username && !url.password
}

/** The body of the request that makes the token: the roles in the order the form lists them, an empty group as none. */
export function inviteRequest(form: InviteForm): JoinTokenRequest {
  return {
    product: form.product,
    roles: joinRoles.filter((r) => form.roles.includes(r)),
    group: form.group.trim() || null,
    minutes: Number(form.minutes.trim()),
    url: form.url.trim().replace(/\/+$/, ''),
  }
}

/** The roles with one turned on or off. */
export function toggleRole(roles: string[], role: string, on: boolean): string[] {
  return on ? (roles.includes(role) ? roles : [...roles, role]) : roles.filter((r) => r !== role)
}

/** The name a person reads for a product id (`runner` is "Runner"). */
export function productLabel(id: string): string {
  return joinProducts.find((p) => p.id === id)?.label ?? id
}

/** "in 14 min", "in 3 h", or "ended": how long a token still works. */
export function endsIn(iso: string, now: number): string {
  const seconds = Math.round((Date.parse(iso) - now) / 1000)
  if (Number.isNaN(seconds)) return 'unknown'
  if (seconds <= 0) return 'ended'
  if (seconds < 90) return `in ${seconds} s`
  const minutes = Math.round(seconds / 60)
  return minutes < 90 ? `in ${minutes} min` : `in ${Math.round(minutes / 60)} h`
}
