import { afterEach, describe, expect, it, vi } from 'vitest'
import { addressOf, canRemove, describeUse, filterInventory, hasInventory, loadInventory, type InventoryRow } from './connectionInventory'
import type { InstanceSummary } from './api'

const server = (id: string, name: string, product: string, reachable = true): InstanceSummary => ({ id, name, url: 'https://x', product, version: '1', apiVersion: 1, reachable, ready: true, problem: null, pairedAt: null })
const build = server('b1', 'Build', 'dbdatabuild')
const sync = server('s1', 'Sync', 'dbdatasync')
const runner = server('r1', 'Runner', 'dbdatarunner')
const proxy = server('p1', 'Proxy', 'dbdataproxy')

afterEach(() => vi.unstubAllGlobals())

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
const answer = (name: string, used_by: object[], extra: object = {}) => ({ connections: [{ name, kind: 'k', engine: 'sqlserver', host: null, database: null, secret_ref: 'env:X', roles: ['run'], used_by, ...extra }] })

describe('the connection inventory', () => {
  it('asks Build, Sync and a runner, through the relay, and no other product', async () => {
    const asked: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      asked.push(url)
      return json(answer(url.includes('/b1/') ? 'wh' : url.includes('/s1/') ? 'erp' : 'src', []))
    }))

    const { rows, problems } = await loadInventory([runner, proxy, sync, build, server('x', 'Down', 'dbdatasync', false)])

    expect(asked.sort()).toEqual(['/relay/b1/api/v1/connections', '/relay/r1/api/v1/connections', '/relay/s1/api/v1/connections'])
    expect(rows.map((r) => `${r.serverName}/${r.name}`)).toEqual(['Build/wh', 'Runner/src', 'Sync/erp'])
    expect(problems).toEqual([])
    expect([build, sync, runner, proxy].map(hasInventory)).toEqual([true, true, true, false])
  })

  it('names a server that does not answer and still shows the others', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => (url.includes('/b1/') ? json({ error: 'too old' }, 404) : json(answer('erp', [])))))

    const { rows, problems } = await loadInventory([build, sync])

    expect(rows.map((r) => r.name)).toEqual(['erp'])
    expect(problems).toHaveLength(1)
    expect(problems[0].server).toBe('Build')
  })

  const row = (name: string, users: { process: string; name: string }[]): InventoryRow => ({
    key: name, server: 's1', serverName: 'Sync', product: 'dbdatasync', name, kind: 'sync', engine: 'sqlserver', host: 'sql01:1433', database: 'Erp', secretRef: 'secret:dbdata:sync:connection:' + name, roles: [],
    usedBy: users.map((u) => ({ ...u, version: null, group: null, instance: null, channel: null, role: 'run' })),
  })
  const rows = [row('erp', [{ process: 'replication', name: 'orders' }, { process: 'server', name: 'this server' }]), row('wh', [{ process: 'replication', name: 'ledger' }])]

  it('answers where a connection is used and what a task connects to', () => {
    expect(filterInventory(rows, { connection: 'ER', user: '' }).map((r) => r.name)).toEqual(['erp'])
    const task = filterInventory(rows, { connection: '', user: 'ledger' })
    expect(task.map((r) => r.name)).toEqual(['wh'])
    // only the users that match are kept
    expect(filterInventory(rows, { connection: '', user: 'orders' })[0].usedBy.map((u) => u.name)).toEqual(['orders'])
    expect(filterInventory(rows, { connection: 'wh', user: 'orders' })).toEqual([])
  })

  it('reads a use in a line, an address without a credential, and removes only on Sync', () => {
    expect(describeUse({ process: 'task', name: 'orders/extract', version: '3', group: 'main', instance: '7f1', channel: 'prod', role: 'run' })).toBe('task orders/extract v3 on main/7f1 (prod), run login')
    expect(addressOf(rows[0])).toBe('sql01:1433/Erp')
    expect(addressOf({ ...rows[0], host: null })).toBe('sqlserver')
    expect(canRemove(rows[0])).toBe(true)
    expect(canRemove({ ...rows[0], product: 'dbdatabuild' })).toBe(false)
  })
})
