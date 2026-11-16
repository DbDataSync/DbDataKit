import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { canCopy, copyFailure, keepsConnections, loadConnections, gatewayConnectionRow, sortConnections } from './connections'
import type { InstanceSummary } from './api'

const server = (id: string, name: string, product: string, reachable = true): InstanceSummary => ({ id, name, url: 'https://x', product, version: '1', apiVersion: 1, reachable, ready: true, problem: null, pairedAt: null })
const second = server('p2', 'Gateway core', 'dbdatagateway')
const gateway = server('p1', 'Gateway edge', 'dbdatagateway')
const build = server('b1', 'Build', 'dbdatabuild')

afterEach(() => vi.unstubAllGlobals())

const answers = (routes: Record<string, () => Response>) =>
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const route = routes[url]
    if (!route) throw new Error(`unexpected call ${url}`)
    return route()
  }))
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('the connections view', () => {
  it('describes a Gateway data source by what it may do and who manages it', () => {
    expect(gatewayConnectionRow(gateway, { name: 'erp', engine: 'sqlserver', access: 'readonly', valid: true, managed_by: null })).toMatchObject({ detail: 'readonly, by hand', product: 'dbdatagateway' })
    expect(gatewayConnectionRow(gateway, { name: 'wh', engine: 'sqlserver', access: 'readwrite', valid: true, managed_by: 'build-1' }).detail).toBe('readwrite, managed by build-1')
    expect(gatewayConnectionRow(gateway, { name: 'bad', engine: null, access: null, valid: false, managed_by: null })).toMatchObject({ valid: false, detail: 'the file is not valid' })
  })

  it('lists the connections of every server that keeps them, by server and name, and skips servers that keep none or do not answer', async () => {
    answers({
      '/relay/p2/api/v1/data-sources': () => json({ data_sources: [{ name: 'zeta', engine: 'postgres', access: 'readonly', valid: true, managed_by: null }, { name: 'alpha', engine: 'sqlserver', access: 'readonly', valid: true, managed_by: null }] }),
      '/relay/p1/api/v1/data-sources': () => json({ data_sources: [{ name: 'erp', engine: 'sqlserver', access: 'readonly', valid: true, managed_by: null }] }),
    })

    const { rows, problems } = await loadConnections([second, gateway, build, server('s1', 'Sync', 'dbdatasync'), server('x', 'Down', 'dbdatagateway', false)])

    expect(problems).toEqual([])
    expect(rows.map((r) => `${r.serverName}/${r.name}`)).toEqual(['Gateway core/alpha', 'Gateway core/zeta', 'Gateway edge/erp'])
  })

  it('names a server whose list is refused, with its words, and still shows the others', async () => {
    answers({
      '/relay/p2/api/v1/data-sources': () => json({ error: 'the list could not be read' }, 400),
      '/relay/p1/api/v1/data-sources': () => json({ data_sources: [{ name: 'erp', engine: 'sqlserver', access: 'readonly', valid: true, managed_by: null }] }),
    })

    const { rows, problems } = await loadConnections([second, gateway])

    expect(rows.map((r) => r.name)).toEqual(['erp'])
    expect(problems).toEqual([{ server: 'Gateway core', message: 'the list could not be read' }])
  })

  it('keeps connections on Gateways that answer, and nowhere else', () => {
    expect([second, gateway, build, server('s1', 'Sync', 'dbdatasync'), server('x', 'Down', 'dbdatagateway', false)].map(keepsConnections)).toEqual([true, true, false, false, false])
    const ds = (name: string) => gatewayConnectionRow(gateway, { name, engine: 'x', access: null, valid: true, managed_by: null })
    expect(sortConnections([ds('b'), ds('a')]).map((r) => r.name)).toEqual(['a', 'b'])
  })

  it('offers a copy only to a chosen server, and to the same server only under another name', () => {
    const row = gatewayConnectionRow(gateway, { name: 'erp', engine: 'sqlserver', access: 'readonly', valid: true, managed_by: null })
    expect(canCopy(row, '', 'erp')).toBe(false)
    expect(canCopy(row, 'p2', 'erp')).toBe(true)
    expect(canCopy(row, 'p2', '  ')).toBe(true)           // empty means the same name, which is free on another server
    expect(canCopy(row, 'p1', 'erp')).toBe(false)
    expect(canCopy(row, 'p1', ' ')).toBe(false)
    expect(canCopy(row, 'p1', 'erp-2')).toBe(true)
  })

  it('reads a refused copy as the sentence, the target\'s diagnostics with their lines, and what the console changed', () => {
    const failure = copyFailure(new ApiError(400, 'Gateway edge did not accept the copy: not valid', { diagnostics: [{ code: 'DBD-CONN-001', severity: 'error', line: 3, message: 'Unknown key `x`.' }], notes: ['managed_by (b) was left out'] }))
    expect(failure).toEqual({ message: 'Gateway edge did not accept the copy: not valid', status: 400, diagnostics: [{ code: 'DBD-CONN-001', severity: 'error', line: 3, message: 'Unknown key `x`.' }], notes: ['managed_by (b) was left out'] })
    expect(copyFailure(new ApiError(409, 'taken', { diagnostics: null }))).toMatchObject({ status: 409, diagnostics: [], notes: [] })
    expect(copyFailure(new Error('offline'))).toMatchObject({ message: 'offline', status: 0 })
  })
})
