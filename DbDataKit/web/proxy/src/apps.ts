import type { AppApprovalRow, AppInstanceRow, AppsCaller, AppSummary, AppVersionRow } from './types'

// The Apps screen's list logic (plan 26, AP24), apart from the screen so it can be tested. Nothing here decides what a person may do: the Proxy enforces its roles on every call. These functions only say, in the
// words the screen shows, why a button is off before a call is made that the Proxy would refuse.

export const NEEDS_PUBLISHER = 'Changing apps needs the publisher or administrator role on this server.'
export const NEEDS_APPROVER = 'Approving or revoking needs the Approver role on this server (assigned to you at the identity home, under this server).'

/** Whether two names are the same person: the audit compares them without regard to case. */
export const samePerson = (a: string, b: string) => a.trim().toLowerCase() === b.trim().toLowerCase()

/** Why a version cannot be approved from here, or null when the call may be tried. The Proxy decides; this is what the Approve button says while it is off. */
export function approveBlock(caller: AppsCaller, version: Pick<AppVersionRow, 'published_by' | 'trust' | 'approval_id'>, by: string): string | null {
  if (!caller.can_approve) return NEEDS_APPROVER
  if (version.trust !== 'trusted') return 'Only an app that asks to be trusted needs an approval; this version is sandboxed.'
  if (version.approval_id) return `An approval already stands for these exact files (${version.approval_id}).`
  if (by.trim() === '') return 'Name the approver: a person of the identity home who holds the Approver role.'
  if (samePerson(by, version.published_by)) return `${version.published_by} published this version, so cannot approve it: the author and the approver are two people.`
  return null
}

/** Why an approval cannot be revoked from here, or null. */
export function revokeBlock(caller: AppsCaller, approval: Pick<AppApprovalRow, 'revoked'>, by: string): string | null {
  if (!caller.can_approve) return NEEDS_APPROVER
  if (approval.revoked) return 'This approval is already revoked.'
  if (by.trim() === '') return 'Name the approver: a person of the identity home who holds the Approver role.'
  return null
}

/** Why an instance cannot be removed, or null. The store's own rules answer the rest (and its message is shown). */
export function removeInstanceBlock(caller: AppsCaller, instance: Pick<AppInstanceRow, 'name' | 'mounted_by'>): string | null {
  if (!caller.can_publish) return NEEDS_PUBLISHER
  if (instance.mounted_by.length > 0) return `The portal ${instance.mounted_by.join(', ')} mounts ${instance.name}: remove or change that first.`
  return null
}

/** Why an app cannot be removed, or null. */
export function removeAppBlock(caller: AppsCaller, app: Pick<AppSummary, 'name' | 'instances'>): string | null {
  if (!caller.can_publish) return NEEDS_PUBLISHER
  if (app.instances.length > 0) return `${app.name} still has instances (${app.instances.join(', ')}): remove them first.`
  return null
}

/** Why an app's served version cannot be moved, or null. */
export function serveBlock(caller: AppsCaller, version: Pick<AppVersionRow, 'serving'>): string | null {
  if (!caller.can_publish) return NEEDS_PUBLISHER
  return version.serving ? 'This version is the one served now.' : null
}

/** Which version an instance runs, in words: following the app or pinned, and when the pin differs from what the app serves, both. */
export function runsLabel(i: Pick<AppInstanceRow, 'follows' | 'pin' | 'app_version' | 'serving_version' | 'problem'>): string {
  if (i.problem) return 'serves nothing'
  if (i.follows) return `follows the app: version ${i.app_version}`
  return i.serving_version != null && i.serving_version !== i.pin ? `pinned to version ${i.pin} (the app serves ${i.serving_version})` : `pinned to version ${i.pin}`
}

/** What an instance's trust says: sandboxed, trusted with the approval that stands, or trusted with none (it does not run as trusted). */
export function trustLabel(i: Pick<AppInstanceRow, 'trust' | 'runs_as_asked' | 'approval_id'>): string {
  if (i.trust !== 'trusted') return 'sandboxed'
  return i.runs_as_asked ? `trusted, approved (${i.approval_id})` : 'asks to be trusted, no approval stands: runs sandboxed or not at all'
}

/** Apps by name; instances by app then name; approvals standing first, newest first, then revoked. */
export const sortApps = (apps: AppSummary[]) => [...apps].sort((a, b) => a.name.localeCompare(b.name))
export const sortInstances = (rows: AppInstanceRow[]) => [...rows].sort((a, b) => a.app.localeCompare(b.app) || a.name.localeCompare(b.name))
export const sortApprovals = (rows: AppApprovalRow[]) => [...rows].sort((a, b) => Number(a.revoked) - Number(b.revoked) || b.approved_at.localeCompare(a.approved_at))

/** The instances of an app. */
export const instancesOf = (rows: AppInstanceRow[], app: string) => rows.filter((i) => i.app === app)

/** An address the screen may link to: http or https only, so nothing the Proxy answers can become a script link. */
export const safeAddress = (address: string | null | undefined): string | null => {
  if (!address) return null
  try { return ['http:', 'https:'].includes(new URL(address).protocol) ? address : null } catch { return null }
}

/** The short form of a content hash, as the command line prints it. */
export const shortHash = (hash: string) => hash.slice(0, 12)
