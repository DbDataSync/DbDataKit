import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { canCopy, copyFailure, keepsConnections, loadConnections, proxyConnectionRow, sortConnections, syncConnectionRow } from './connections'
import type { InstanceSummary } from './api'

const server = (id: string, name: string, product: string, reachable = true): InstanceSummary => ({ id, name, url: 'https://x', product, version: '1', apiVersion: 1, reachable, ready: true, problem: null, pairedAt: null })
const sync = server('s1', 'Sync prod', 'dbdatasync')
const proxy = server('p1', 'Proxy edge', 'dbdataproxy')
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
  it('describes a Sync connection by its address and a Proxy data source by what it may do and who manages it', () => {
    expect(syncConnectionRow(sync, { name: 'erp', driverType: 'sqlserver', host: 'sql01', port: 1433, database: 'Erp', connectionString: null })).toMatchObject({ engine: 'sqlserver', detail: 'sql01:1433/Erp', valid: true, product: 'dbdatasync' })
    // a connection string can hold what should not be shown, so it is not
    expect(syncConnectionRow(sync, { name: 'x', driverType: 'postgres', host: null, port: null, database: null, connectionString: 'Host=a;Password=hunter2' }).detail).toBe('connection string')
    expect(proxyConnectionRow(proxy, { name: 'erp', engine: 'sqlserver', access: 'readonly', valid: true, managed_by: null })).toMatchObject({ detail: 'readonly, by hand', product: 'dbdataproxy' })
    expect(proxyConnectionRow(proxy, { name: 'wh', engine: 'sqlserver', access: 'readwrite', valid: true, managed_by: 'build-1' }).detail).toBe('readwrite, managed by build-1')
    expect(proxyConnectionRow(proxy, { name: 'bad', engine: null, access: null, valid: false, managed_by: null })).toMatchObject({ valid: false, detail: 'the file is not valid' })
  })

  it('lists the connections of every server that keeps them, by server and name, and skips servers that keep none or do not answer', async () => {
    answers({
      '/relay/s1/api/connections': () => json([
        { name: 'zeta', driverType: 'postgres', host: 'h', port: null, database: null, connectionString: null },
        { name: 'alpha', driverType: 'sqlserver', host: 'h', port: 1, database: 'd', connectionString: null },
      ]),
      '/relay/p1/api/v1/data-sources': () => json({ data_sources: [{ name: 'erp', engine: 'sqlserver', access: 'readonly', valid: true, managed_by: null }] }),
    })

    const { rows, problems } = await loadConnections([sync, proxy, build, server('x', 'Down', 'dbdatasync', false)])

    expect(problems).toEqual([])
    expect(rows.map((r) => `${r.serverName}/${r.name}`)).toEqual(['Proxy edge/erp', 'Sync prod/alpha', 'Sync prod/zeta'])
  })

  it('names a server whose list is refused, with its words, and still shows the others', async () => {
    answers({
      '/relay/s1/api/connections': () => json({ error: 'old.yaml is a connection in the form of earlier releases' }, 400),
      '/relay/p1/api/v1/data-sources': () => json({ data_sources: [{ name: 'erp', engine: 'sqlserver', access: 'readonly', valid: true, managed_by: null }] }),
    })

    const { rows, problems } = await loadConnections([sync, proxy])

    expect(rows.map((r) => r.name)).toEqual(['erp'])
    expect(problems).toEqual([{ server: 'Sync prod', message: 'old.yaml is a connection in the form of earlier releases' }])
  })

  it('keeps connections on Sync servers and Proxies that answer, and nowhere else', () => {
    expect([sync, proxy, build, server('x', 'Down', 'dbdatasync', false)].map(keepsConnections)).toEqual([true, true, false, false])
    expect(sortConnections([syncConnectionRow(sync, { name: 'b', driverType: 'x', host: null, port: null, database: null, connectionString: 'c' }), syncConnectionRow(sync, { name: 'a', driverType: 'x', host: null, port: null, database: null, connectionString: 'c' })]).map((r) => r.name)).toEqual(['a', 'b'])
  })

  it('offers a copy only to a chosen server, and to the same server only under another name', () => {
    const row = syncConnectionRow(sync, { name: 'erp', driverType: 'sqlserver', host: 'h', port: null, database: null, connectionString: null })
    expect(canCopy(row, '', 'erp')).toBe(false)
    expect(canCopy(row, 'p1', 'erp')).toBe(true)
    expect(canCopy(row, 'p1', '  ')).toBe(true)           // empty means the same name, which is free on another server
    expect(canCopy(row, 's1', 'erp')).toBe(false)
    expect(canCopy(row, 's1', ' ')).toBe(false)
    expect(canCopy(row, 's1', 'erp-2')).toBe(true)
  })

  it('reads a refused copy as the sentence, the target\'s diagnostics with their lines, and what the console changed', () => {
    const failure = copyFailure(new ApiError(400, 'Proxy edge did not accept the copy: not valid', { diagnostics: [{ code: 'DBD-CONN-001', severity: 'error', line: 3, message: 'Unknown key `x`.' }], notes: ['managed_by (b) was left out'] }))
    expect(failure).toEqual({ message: 'Proxy edge did not accept the copy: not valid', status: 400, diagnostics: [{ code: 'DBD-CONN-001', severity: 'error', line: 3, message: 'Unknown key `x`.' }], notes: ['managed_by (b) was left out'] })
    expect(copyFailure(new ApiError(409, 'taken', { diagnostics: null }))).toMatchObject({ status: 409, diagnostics: [], notes: [] })
    expect(copyFailure(new Error('offline'))).toMatchObject({ message: 'offline', status: 0 })
  })
})
