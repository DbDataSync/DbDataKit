import { afterEach, describe, expect, it, vi } from 'vitest'
import { appRows, decideRequest, mcpDecisionPath, mcpRows, decisionPath, decisionProblem, sortApprovals, storeRows, waited, type ApprovalRow, type GatewayAppDetail, type StoreRequestList } from './approvals'
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

afterEach(() => vi.unstubAllGlobals())

describe('deciding a change request here (SC21c)', () => {
  const pending = (instance = 'default'): ApprovalRow => storeRows(store, 'budgets', list(request({ instance })))[0]!

  it('carries which request a row is, so it can be decided without opening the Store\'s screen', () => {
    expect(pending().request).toEqual({ model: 'budgets', instance: 'default', id: 'r1' })
    expect(appRows(gateway, detail(null, version(1)))[0]!.request).toBeUndefined()
  })

  it('addresses the Store\'s own approve and reject routes through the relay, the instance in the path when it is not the default', () => {
    expect(decisionPath('st1', pending().request!, true)).toBe('/relay/st1/api/v1/models/budgets/requests/r1/approve')
    expect(decisionPath('st1', pending('north').request!, false)).toBe('/relay/st1/api/v1/models/budgets/instances/north/requests/r1/reject')
  })

  it('asks for the reason before a rejection, never before an approval, and refuses what is not the person\'s to decide', () => {
    expect(decisionProblem(pending(), true, '')).toBeNull()
    expect(decisionProblem(pending(), false, '  ')).toMatch(/Say why/)
    expect(decisionProblem(pending(), false, 'not this quarter')).toBeNull()
    expect(decisionProblem({ ...pending(), canDecide: false }, true, '')).toMatch(/may not/)
    expect(decisionProblem(appRows(gateway, detail(null, version(1)))[0]!, true, '')).toMatch(/own screen/)
  })

  it('approves through the relay with the note the person wrote', async () => {
    const calls: { url: string; init: RequestInit }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init: RequestInit) => { calls.push({ url, init }); return new Response(JSON.stringify({ id: 'r1', state: 'approved' }), { status: 200, headers: { 'Content-Type': 'application/json' } }) }))
    await decideRequest(pending(), true, ' fine ')
    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toBe('/relay/st1/api/v1/models/budgets/requests/r1/approve')
    expect(calls[0]!.init.method).toBe('POST')
    expect(JSON.parse(calls[0]!.init.body as string)).toEqual({ note: 'fine' })
  })

  it('sends an empty body for an approval with no note, and carries the Store\'s own sentence when it refuses', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ error: 'The row no longer exists' }), { status: 409, headers: { 'Content-Type': 'application/json' } })))
    await expect(decideRequest(pending(), true, '')).rejects.toThrow('The row no longer exists')
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)
    await decideRequest(pending(), false, 'no')
    expect(JSON.parse((fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ note: 'no' })
  })

  it('does not call the server for an app version', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    await expect(decideRequest(appRows(gateway, detail(null, version(1)))[0]!, true, '')).rejects.toThrow(/own screen/)
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

describe('the steps agents asked through a Gateway\'s mcp module (AG33)', () => {
  const step = (over = {}) => ({ id: 'a1', status: 'pending', tool: 'write_entity_changes', summary: 'Write 2 change(s) to the master data model \'budgets\'.', asked_by: 'u1', agent: 'loader', arguments: {}, asked_at: '2026-11-10T10:00:00Z', ...over })

  it('lists each waiting step as an agent\'s, with the module\'s sentence, and leaves out an answered one', () => {
    const rows = mcpRows(gateway, { approvals: [step(), step({ id: 'a2', status: 'approved' })] })
    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({ kind: 'mcp-step', subject: 'write_entity_changes', by: 'loader', byAgent: true, canDecide: true, mcp: { id: 'a1' }, product: 'dbdatagateway' })
    expect(rows[0]!.detail).toContain('budgets')
  })

  it('answers through the relay to the module\'s own routes, with the note when there is one, and needs no note to deny', async () => {
    const [row] = mcpRows(gateway, { approvals: [step()] })
    expect(mcpDecisionPath('gw1', 'a1', true)).toBe('/relay/gw1/api/v1/mcp/approvals/a1/approve')
    expect(decisionProblem(row!, false, '')).toBeNull()
    const fetchMock = vi.fn(async () => new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)
    await decideRequest(row!, false, ' not now ')
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe('/relay/gw1/api/v1/mcp/approvals/a1/deny')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ note: 'not now' })
  })
})
