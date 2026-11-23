import { afterEach, describe, expect, it, vi } from 'vitest'
import { digestOf } from './hooks'
import { MAX_UNSAVED_FILES, isQuestionPath, syncApi, unsavedOf } from './syncApi'

describe('the unsaved edits a question carries', () => {
  it('are the catalog cache, the files under config/ and a driver.yaml, with their texts', () => {
    expect(['catalog-cache.yaml', 'config/connections/erp.yaml', 'drivers/legacy/driver.yaml', 'drivers/legacy/driver.csx', 'README.md', 'scripts/x.csx'].filter(isQuestionPath))
      .toEqual(['catalog-cache.yaml', 'config/connections/erp.yaml', 'drivers/legacy/driver.yaml'])
    const texts: Record<string, string> = { 'config/b.yaml': 'b', 'catalog-cache.yaml': 'c', 'README.md': 'r' }
    expect(unsavedOf(['README.md', 'config/b.yaml', 'catalog-cache.yaml', 'config/gone.yaml'], (p) => texts[p]))
      .toEqual({ 'catalog-cache.yaml': 'c', 'config/b.yaml': 'b' })
  })

  it('are at most as many as the route takes', () => {
    const paths = Array.from({ length: MAX_UNSAVED_FILES + 5 }, (_, i) => `config/f${String(i).padStart(4, '0')}.yaml`)
    expect(Object.keys(unsavedOf(paths, () => 'x'))).toHaveLength(MAX_UNSAVED_FILES)
  })

  it('give the same digest for the same edits and another for a changed text', () => {
    expect(digestOf({ a: 'x' })).toBe(digestOf({ a: 'x' }))
    expect(digestOf({ a: 'x' })).not.toBe(digestOf({ a: 'y' }))
    expect(digestOf({})).toBe(digestOf({}))
  })
})

describe('the client of the Workspace\'s sync routes', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('asks the draft\'s sync routes, with the unsaved edits only when there are some', async () => {
    const calls: { url: string; method: string; body: unknown }[] = []
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({ url: String(input), method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : null })
      return Promise.resolve(new Response(JSON.stringify({ slots: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    }))
    await syncApi.scriptSlots('plant sync', 'ws-1')
    await syncApi.capabilities('plant sync', 'ws-1', 'erp')
    await syncApi.inferredColumns('p', 'w', 'orders', 'dbo.orders', { 'catalog-cache.yaml': 'c' })
    await syncApi.bulkCreate('p', 'w', 'orders', [{ schema: 'dbo', table: 'customers' }])
    expect(calls).toEqual([
      { url: '/api/v1/projects/plant%20sync/workspaces/ws-1/sync/script-slots', method: 'GET', body: null },
      { url: '/api/v1/projects/plant%20sync/workspaces/ws-1/sync/capabilities', method: 'POST', body: { connection: 'erp' } },
      { url: '/api/v1/projects/p/workspaces/w/sync/inferred-columns', method: 'POST', body: { replication: 'orders', mapping: 'dbo.orders', unsaved: { 'catalog-cache.yaml': 'c' } } },
      { url: '/api/v1/projects/p/workspaces/w/sync/bulk-create', method: 'POST', body: { replication: 'orders', tables: [{ schema: 'dbo', table: 'customers' }] } },
    ])
  })
})
