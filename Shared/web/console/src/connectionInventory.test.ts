import { afterEach, describe, expect, it, vi } from 'vitest'
import { addressOf, describeUse, filterInventory, hasInventory, inventoryRows, loadInventory, runnerCopyState, type InventoryRow } from './connectionInventory'
import type { InstanceSummary } from './api'

const server = (id: string, name: string, product: string, reachable = true): InstanceSummary => ({ id, name, url: 'https://x', product, version: '1', apiVersion: 1, reachable, ready: true, problem: null, pairedAt: null })
const runner = server('r1', 'Runner', 'dbdatarunner')
const second = server('r2', 'Runner two', 'dbdatarunner')
const gateway = server('p1', 'Gateway', 'dbdatagateway')

afterEach(() => vi.unstubAllGlobals())

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
const answer = (name: string, used_by: object[], extra: object = {}) => ({ connections: [{ name, kind: 'k', engine: 'sqlserver', host: null, database: null, secret_ref: 'env:X', roles: ['run'], used_by, ...extra }] })

describe('the connection inventory', () => {
  it('asks each runner, through the relay, and no other product', async () => {
    const asked: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      asked.push(url)
      return json(answer(url.includes('/r2/') ? 'erp' : 'src', []))
    }))

    const { rows, problems } = await loadInventory([runner, gateway, second, server('b1', 'Build', 'dbdatabuild'), server('x', 'Down', 'dbdatarunner', false)])

    expect(asked.sort()).toEqual(['/relay/r1/api/v1/connections', '/relay/r2/api/v1/connections'])
    expect(rows.map((r) => `${r.serverName}/${r.name}`)).toEqual(['Runner/src', 'Runner two/erp'])
    expect(problems).toEqual([])
    expect([runner, gateway, server('s1', 'Sync', 'dbdatasync')].map(hasInventory)).toEqual([true, false, false])
  })

  it('names a runner that does not answer and still shows the others', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => (url.includes('/r1/') ? json({ error: 'too old' }, 404) : json(answer('erp', [])))))

    const { rows, problems } = await loadInventory([runner, second])

    expect(rows.map((r) => r.name)).toEqual(['erp'])
    expect(problems).toHaveLength(1)
    expect(problems[0].server).toBe('Runner')
  })

  const row = (name: string, users: { process: string; name: string }[]): InventoryRow => ({
    key: name, server: 'r2', serverName: 'Runner two', product: 'dbdatarunner', name, kind: 'database', engine: 'sqlserver', host: 'sql01:1433', database: 'Erp', secretRef: 'secret:dbdatakit:connection:' + name, roles: [], source: null, version: null,
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

  it('reads a use in a line, and an address without a credential', () => {
    expect(describeUse({ process: 'task', name: 'orders/extract', version: '3', group: 'main', instance: '7f1', channel: 'prod', role: 'run' })).toBe('task orders/extract v3 on main/7f1 (prod), run login')
    expect(addressOf(rows[0])).toBe('sql01:1433/Erp')
    expect(addressOf({ ...rows[0], host: null })).toBe('sqlserver')
  })

  // plan 28c E27: a runner's rows say where the definition came from and its version; the table marks it against the console's file
  const onRunner = (name: string, source: string | null, version: string | null): InventoryRow => ({ ...row(name, []), serverName: 'Runner', server: 'r1', product: 'dbdatarunner', source, version })
  const consoleHas = (name: string, version: string) => ({ name, kind: 'database', purpose: null, models: [], canSend: true, target: null, cloud: null, version })

  it('reads engine, host, database, source and version from a runner answer', () => {
    const read = inventoryRows(runner, { connections: [{ name: 'erp', kind: 'database', engine: 'sqlserver', host: 'sql01:1433', database: 'Erp', secret_ref: 'env:X', roles: [], used_by: [], source: 'copy', version: 'sha256:aa' }, { name: 'old', kind: 'k', engine: null, host: null, database: null, secret_ref: null, roles: [], used_by: [] }] })
    expect(read[0]).toMatchObject({ engine: 'sqlserver', host: 'sql01:1433', database: 'Erp', source: 'copy', version: 'sha256:aa' })
    expect(read[1]).toMatchObject({ source: null, version: null })
  })

  it("marks a runner copy current, stale, missing or the runner's own against the console file", () => {
    const mine = [consoleHas('erp', 'sha256:aa')]
    expect(runnerCopyState(onRunner('erp', 'copy', 'sha256:aa'), mine)).toBe('current')
    expect(runnerCopyState(onRunner('erp', 'copy', 'sha256:bb'), mine)).toBe('stale')
    expect(runnerCopyState(onRunner('erp', 'unknown', null), mine)).toBe('missing')
    expect(runnerCopyState(onRunner('erp', 'file', 'sha256:aa'), mine)).toBe("runner's own")
    expect(runnerCopyState(onRunner('own', 'copy', 'sha256:aa'), mine)).toBe("runner's own")
    expect(runnerCopyState(onRunner('env:X', 'reference', null), mine)).toBe("runner's own")
    // nothing to compare: a name nobody holds, a row that says no source
    expect(runnerCopyState(onRunner('ghost', 'unknown', null), mine)).toBeNull()
    expect(runnerCopyState(row('erp', []), mine)).toBeNull()
  })

  it('filters by the server a connection exists on, and shows an unused copy', () => {
    const all = [onRunner('spare', 'copy', 'sha256:aa'), row('erp', [{ process: 'replication', name: 'orders' }])]
    expect(filterInventory(all, { connection: '', user: '', server: 'runner' }).map((r) => r.name)).toEqual(['spare', 'erp'])
    expect(filterInventory(all, { connection: '', user: '', server: 'two' }).map((r) => r.name)).toEqual(['erp'])
    expect(filterInventory(all, { connection: '', user: '' })).toHaveLength(2)
    expect(all[0].usedBy).toEqual([])
  })
})
