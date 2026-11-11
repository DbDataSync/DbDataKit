import { describe, expect, it } from 'vitest'
import { appRows, sortApprovals, storeRows, waited, type ApprovalRow, type GatewayAppDetail, type StoreRequestList } from './approvals'
import type { InstanceSummary } from './api'

const server = (id: string, name: string, product: string): InstanceSummary => ({ id, name, url: 'https://x', product, version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null })
const store = server('st1', 'Master data', 'dbdatastore')
const gateway = server('gw1', 'Gateway prod', 'dbdatagateway')

const request = (over: Partial<StoreRequestList['requests'][number]> = {}) => ({
  id: 'r1', model: 'budgets', instance: 'default', state: 'pending', proposed_by: 'u1', proposed_by_name: 'Ada', by_agent: false, proposed_at: '2026-11-10T10:00:00Z', description: 'Add B1', change_count: 1, ...over,
})
const list = (...requests: StoreRequestList['requests']): StoreRequestList => ({ requests, can_decide: true, decision_needs: 'author' })

describe('a Store model\'s change requests', () => {
  it('shows each pending request with who proposed it, how many changes, and the screen that decides it', () => {
    const [row] = storeRows(store, 'budgets', list(request()))
    expect(row).toMatchObject({ kind: 'change-request', subject: 'budgets', detail: 'Add B1 (1 change)', by: 'Ada', byAgent: false, canDecide: true, link: '/servers/st1/model/budgets/changes' })
  })

  it('leaves out a decided request, names an instance, and falls back to the subject id when no name is known', () => {
    const rows = storeRows(store, 'budgets', list(request({ state: 'approved' }), request({ id: 'r2', instance: 'north', proposed_by_name: null, description: null, change_count: 3, by_agent: true })))
    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({ subject: 'budgets (instance north)', detail: '3 changes', by: 'u1', byAgent: true })
  })

  it('says a person who may not decide may not, as the server said it', () => {
    expect(storeRows(store, 'budgets', { ...list(request()), can_decide: false })[0]!.canDecide).toBe(false)
  })
})

const version = (n: number, over: Partial<GatewayAppDetail['versions'][number]> = {}) => ({ version: n, trust: 'trusted' as const, published_by: 'Bo', published_at: '2026-11-09T08:00:00Z', serving: false, approval_id: null, ...over })
const detail = (serving: number | null, ...versions: GatewayAppDetail['versions']): GatewayAppDetail => ({ name: 'crm', serving, versions, caller: { can_approve: false } })

describe('a Gateway app asking to be trusted', () => {
  it('waits when the served or a newer version is trusted and has no approval', () => {
    const rows = appRows(gateway, detail(2, version(1), version(2, { serving: true }), version(3)))
    expect(rows.map((r) => r.subject)).toEqual(['crm version 2', 'crm version 3'])
    expect(rows[0]!.detail).toMatch(/Served now/)
    expect(rows[0]).toMatchObject({ kind: 'app-trust', by: 'Bo', canDecide: false, link: '/servers/gw1/apps' })
  })

  it('does not wait when it is sandboxed or an approval stands', () => {
    expect(appRows(gateway, detail(1, version(1, { trust: 'sandbox', serving: true }), version(2, { approval_id: 'ap-1' })))).toEqual([])
  })

  it('counts every version when nothing is served yet', () => {
    expect(appRows(gateway, detail(null, version(1)))).toHaveLength(1)
  })
})

describe('the order and the wait', () => {
  const row = (over: Partial<ApprovalRow>): ApprovalRow => ({ key: 'k', server: 's', serverName: 'A', product: 'dbdatastore', kind: 'change-request', subject: 's', detail: '', by: null, byAgent: false, since: null, canDecide: true, link: '/', ...over })

  it('puts the longest wait first, an agent before a person of the same age, and an unknown time last', () => {
    const sorted = sortApprovals([
      row({ subject: 'unknown' }),
      row({ subject: 'newer', since: '2026-11-10T12:00:00Z' }),
      row({ subject: 'person', since: '2026-11-10T10:00:00Z' }),
      row({ subject: 'agent', since: '2026-11-10T10:00:00Z', byAgent: true }),
    ])
    expect(sorted.map((r) => r.subject)).toEqual(['agent', 'person', 'newer', 'unknown'])
  })

  it('says how long in words', () => {
    const now = Date.parse('2026-11-10T12:00:00Z')
    expect(waited('2026-11-10T11:59:40Z', now)).toBe('under a minute')
    expect(waited('2026-11-10T11:30:00Z', now)).toBe('30 min')
    expect(waited('2026-11-10T07:00:00Z', now)).toBe('5 h')
    expect(waited('2026-11-05T12:00:00Z', now)).toBe('5 days')
    expect(waited(null, now)).toBe('')
    expect(waited('not a date', now)).toBe('')
  })
})
