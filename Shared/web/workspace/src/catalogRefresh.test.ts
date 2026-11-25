import { describe, expect, it } from 'vitest'
import { readCatalogCache } from './catalogCache'
import { defaultChannel, defaultRunner, differenceLine, lastRunnerRead, refreshOutcome, unboundLine, workspaceServerOf } from './catalogRefresh'

const cache = (connections: string) => ['schema: catalog-cache/2', "generated_at: '2026-11-20T09:00:00Z'", 'connections:', connections, ''].join('\n')

const crm = (accounts: string, extra = '') => [
  "  'crm':",
  "    engine: 'sqlserver'",
  "    read: {server: 'runners-eu', product: 'runner', channel: 'dev', at: '2026-11-20T09:00:00Z'}",
  '    databases:',
  "      - {name: 'crm', schemas: ['dbo', 'staging']}",
  '    tables:',
  "      - database: 'crm'",
  "        schema: 'dbo'",
  "        name: 'accounts'",
  '        kind: table',
  '        columns:',
  accounts,
  extra,
].filter((l) => l !== '').join('\n')

const id = "          - {name: 'id', type: 'int', nullable: false, key: true}"
const name = "          - {name: 'name', type: 'nvarchar(100)', nullable: true}"
const orders = [
  "      - database: 'crm'", "        schema: 'dbo'", "        name: 'orders'", '        kind: table', '        columns:', "          - {name: 'id', type: 'int', nullable: false, key: true}",
].join('\n')
const dw = ["  'dw':", "    engine: 'postgres'", "    read: {server: 'gw', product: 'gateway', at: '2026-11-19T09:00:00Z'}", '    databases: []', '    tables: []'].join('\n')

/** What the console answers for a read: one connection, read later. */
const answer = (accounts: string, extra = '') => cache(crm(accounts, extra).replace("at: '2026-11-20T09:00:00Z'", "at: '2026-11-22T10:00:04Z'")).replace("generated_at: '2026-11-20T09:00:00Z'", "generated_at: '2026-11-22T10:00:04Z'")

describe('a refresh from a Runner channel', () => {
  it('finds the Workspace server of a module mounted through the relay, and none outside a console', () => {
    expect(workspaceServerOf('/relay/ws-1')).toBe('ws-1')
    expect(workspaceServerOf('')).toBeNull()
  })

  it('reports a column added to a table as the difference, and the accepted cache keeps the other connection and is recorded as read in dev', () => {
    const held = cache([crm(`${id}\n${name}`), dw].join('\n'))
    const outcome = refreshOutcome(held, answer(`${id}\n${name}\n          - {name: 'segment', type: 'nvarchar(20)', nullable: true}`), 'crm', { schema: 'dbo', name: 'accounts' })
    expect(outcome.ok ? '' : outcome.problem).toBe('')
    if (!outcome.ok) return
    expect(differenceLine(outcome.difference)).toBe('1 column added')
    expect(outcome.difference.columnsAdded).toEqual([{ table: 'crm.dbo.accounts', column: 'segment', to: 'nvarchar(20)' }])
    const after = readCatalogCache(outcome.text)
    expect(after.findings).toEqual([])
    expect(Object.keys(after.cache!.connections)).toEqual(['crm', 'dw'])
    expect(after.cache!.connections.crm!.read).toEqual({ server: 'runners-eu', product: 'runner', channel: 'dev', at: '2026-11-22T10:00:04Z' })
    expect(after.cache!.connections.crm!.tables[0]!.columns.map((c) => c.name)).toEqual(['id', 'name', 'segment'])
  })

  it('reads a removed column, a retyped one and a table that is gone; a read of one table keeps the connection\'s other tables', () => {
    const held = cache(crm(`${id}\n${name}`, orders))
    const narrower = refreshOutcome(held, answer("          - {name: 'id', type: 'bigint', nullable: false, key: true}"), 'crm', { schema: 'dbo', name: 'accounts' })
    if (!narrower.ok) throw new Error(narrower.problem)
    expect(differenceLine(narrower.difference)).toBe('1 column removed, 1 column retyped')
    expect(narrower.difference.columnsRetyped).toEqual([{ table: 'crm.dbo.accounts', column: 'id', from: 'int not null', to: 'bigint not null' }])
    expect(readCatalogCache(narrower.text).cache!.connections.crm!.tables.map((t) => t.name)).toEqual(['accounts', 'orders'])

    // the whole connection read again: orders is no longer there
    const whole = refreshOutcome(held, answer(`${id}\n${name}`), 'crm')
    if (!whole.ok) throw new Error(whole.problem)
    expect(whole.difference.tablesRemoved).toEqual(['crm.dbo.orders'])
    expect(differenceLine(whole.difference)).toBe('1 table removed')
  })

  it('makes the cache when the draft has none, and says nothing changed when nothing did', () => {
    const first = refreshOutcome(undefined, answer(id), 'crm')
    if (!first.ok) throw new Error(first.problem)
    expect(first.difference.tablesAdded).toEqual(['crm.dbo.accounts'])
    const again = refreshOutcome(first.text, answer(id), 'crm')
    if (!again.ok) throw new Error(again.problem)
    expect(differenceLine(again.difference)).toBe('no change')
  })

  it('does not replace a cache the strict reader refuses, so no connection of it is dropped unseen', () => {
    const old = ['schema: catalog-cache/1', 'generated_at: 2026-11-01T00:00:00Z', 'connections: {}', ''].join('\n')
    const outcome = refreshOutcome(old, answer(id), 'crm')
    expect(outcome.ok).toBe(false)
    if (outcome.ok) return
    expect(outcome.problem).toContain('Delete the file or fix it')
  })
})

describe('where a refresh reads by default', () => {
  const list = {
    package: 'crm-to-dw', connection: 'crm', slot: 'crm', runner: 'r-1', runner_name: 'runners-eu', default: 'dev',
    channels: [{ channel: 'dev', production: false, bound: true, bound_to: 'crm_dev' }, { channel: 'prod', production: true, bound: true, bound_to: 'crm' }, { channel: 'test', production: false, bound: false, bound_to: null }],
  }
  it('reads again where the cache says the connection was read, else the first channel that is not production and binds the slot', () => {
    expect(defaultChannel(list, 'prod')).toBe('prod')
    expect(defaultChannel(list, 'test')).toBe('dev')   // no longer bound there
    expect(defaultChannel(list, null)).toBe('dev')
    expect(defaultChannel({ ...list, default: null })).toBe('')
    expect(defaultRunner([{ id: 'r-1', name: 'runners-eu' }, { id: 'r-2', name: 'runners-us' }], 'runners-us')).toBe('r-2')
    expect(defaultRunner([{ id: 'r-1', name: 'runners-eu' }], 'gone')).toBe('r-1')
    expect(lastRunnerRead(cache(crm(id)), 'crm')).toEqual({ server: 'runners-eu', channel: 'dev' })
    expect(lastRunnerRead(cache(dw), 'dw')).toBeNull()   // read through a Gateway
  })
})

describe('a channel that does not bind the slot', () => {
  it('names Set up for development for every channel, production as any other', () => {
    expect(unboundLine({ channel: 'dev', production: false, bound: false, bound_to: null }, 'crm')).toBe(
      'dev does not bind the slot crm. Set up for development binds it with a login you type, or an administrator binds it on the Runner\'s Packages page (Bindings), then the read can be asked.')
    expect(unboundLine({ channel: 'prod', production: true, bound: false, bound_to: null }, 'crm')).toBe(
      'prod does not bind the slot crm. Set up for development binds it with a login you type, or an administrator binds it on the Runner\'s Packages page (Bindings), then the read can be asked.')
  })
})
