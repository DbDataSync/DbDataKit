import { describe, expect, it } from 'vitest'
import {
  approveBlock, NEEDS_APPROVER, NEEDS_PUBLISHER, removeAppBlock, removeInstanceBlock, revokeBlock, runsLabel, safeAddress, serveBlock, sortApprovals, sortApps, sortInstances, trustLabel,
} from './apps'
import type { AppApprovalRow, AppInstanceRow, AppsCaller, AppSummary } from './types'

const viewer: AppsCaller = { can_publish: false, can_approve: false, you: 'Ada' }
const admin: AppsCaller = { can_publish: true, can_approve: false, you: 'Ada' }
const approver: AppsCaller = { can_publish: false, can_approve: true, you: 'Grace' }
const trusted = { trust: 'trusted' as const, published_by: 'ada-laptop', approval_id: null }

describe('why a button is off', () => {
  it('approving needs the Approver role first, then a trusted version with no approval, a named approver who is not the author', () => {
    expect(approveBlock(viewer, trusted, 'Grace')).toBe(NEEDS_APPROVER)
    expect(approveBlock(admin, trusted, 'Grace')).toBe(NEEDS_APPROVER)
    expect(approveBlock(approver, { ...trusted, trust: 'sandbox' }, 'Grace')).toContain('sandboxed')
    expect(approveBlock(approver, { ...trusted, approval_id: 'apr_1' }, 'Grace')).toContain('apr_1')
    expect(approveBlock(approver, trusted, '  ')).toContain('Name the approver')
    expect(approveBlock(approver, trusted, 'ADA-Laptop')).toContain('two people')
    expect(approveBlock(approver, trusted, 'Grace Hopper')).toBeNull()
  })

  it('revoking needs the Approver role, a standing approval and a named approver', () => {
    expect(revokeBlock(admin, { revoked: false }, 'Grace')).toBe(NEEDS_APPROVER)
    expect(revokeBlock(approver, { revoked: true }, 'Grace')).toContain('already revoked')
    expect(revokeBlock(approver, { revoked: false }, '')).toContain('Name the approver')
    expect(revokeBlock(approver, { revoked: false }, 'Grace')).toBeNull()
  })

  it('removing an instance needs the publisher role and no portal that mounts it; an app needs no instance left', () => {
    expect(removeInstanceBlock(viewer, { name: 'acme', mounted_by: [] })).toBe(NEEDS_PUBLISHER)
    expect(removeInstanceBlock(admin, { name: 'acme', mounted_by: ['front'] })).toContain('front mounts acme')
    expect(removeInstanceBlock(admin, { name: 'acme', mounted_by: [] })).toBeNull()
    expect(removeAppBlock(viewer, { name: 'notes', instances: [] })).toBe(NEEDS_PUBLISHER)
    expect(removeAppBlock(admin, { name: 'notes', instances: ['acme', 'pinned'] })).toContain('acme, pinned')
    expect(removeAppBlock(admin, { name: 'notes', instances: [] })).toBeNull()
  })

  it('serving a version (the rollback) needs the publisher role and a version that is not serving already', () => {
    expect(serveBlock(viewer, { serving: false })).toBe(NEEDS_PUBLISHER)
    expect(serveBlock(admin, { serving: true })).toContain('served now')
    expect(serveBlock(admin, { serving: false })).toBeNull()
  })
})

describe('what an instance runs', () => {
  it('says follow, pin, and a pin that differs from what the app serves', () => {
    expect(runsLabel({ follows: true, pin: null, app_version: 2 })).toBe('follows the app: version 2')
    expect(runsLabel({ follows: false, pin: 1, app_version: 1, serving_version: 1 })).toBe('pinned to version 1')
    expect(runsLabel({ follows: false, pin: 1, app_version: 1, serving_version: 2 })).toBe('pinned to version 1 (the app serves 2)')
    expect(runsLabel({ follows: true, pin: null, problem: 'gone' })).toBe('serves nothing')
  })

  it('names the trust: sandboxed, approved, or asking with no approval', () => {
    expect(trustLabel({ trust: 'sandbox' })).toBe('sandboxed')
    expect(trustLabel({ trust: 'trusted', runs_as_asked: true, approval_id: 'apr_9' })).toBe('trusted, approved (apr_9)')
    expect(trustLabel({ trust: 'trusted', runs_as_asked: false })).toContain('no approval stands')
  })

  it('links only to http and https addresses', () => {
    expect(safeAddress('https://apps.example:8818/apps/acme/')).toBe('https://apps.example:8818/apps/acme/')
    expect(safeAddress('javascript:alert(1)')).toBeNull()
    expect(safeAddress('not a url')).toBeNull()
    expect(safeAddress(null)).toBeNull()
  })
})

describe('the order of the lists', () => {
  const app = (name: string) => ({ name } as AppSummary)
  const instance = (name: string, a: string) => ({ name, app: a } as AppInstanceRow)
  const approval = (id: string, at: string, revoked: boolean) => ({ id, approved_at: at, revoked } as AppApprovalRow)

  it('lists apps by name, instances by app then name, and standing approvals first, newest first', () => {
    expect(sortApps([app('b'), app('a')]).map((a) => a.name)).toEqual(['a', 'b'])
    expect(sortInstances([instance('z', 'a'), instance('y', 'b'), instance('x', 'a')]).map((i) => i.name)).toEqual(['x', 'z', 'y'])
    expect(sortApprovals([approval('old', '2026-01-01', false), approval('gone', '2026-03-01', true), approval('new', '2026-02-01', false)]).map((a) => a.id)).toEqual(['new', 'old', 'gone'])
  })
})
