import { describe, expect, it } from 'vitest'
import { appSemanticClient, appStoreClient, loadAll } from './data'
import { rows } from './fixtures'

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('reading through the app', () => {
  it('goes to the instance\'s data route with the shell\'s token, for rows, one row, changes and the schema', async () => {
    const seen: { url: string; auth: string | null; method: string }[] = []
    const fake = (async (url: string, init?: RequestInit) => {
      seen.push({ url, auth: new Headers(init?.headers).get('Authorization'), method: init?.method ?? 'GET' })
      return url.endsWith('/changes') ? json({ applied: 1 }) : url.includes('/row?') ? json(rows[0]) : url.endsWith('/schema') ? json({ name: 'territories', entities: [] }) : json({ rows: [], limit: 5, offset: 0 })
    }) as unknown as typeof fetch
    const client = appStoreClient({ instance: 'tv', token: async () => 'tok' }, fake)
    await client.schema('territories')
    await client.rows('territories', 'territory', { limit: 5, offset: 10 })
    await client.row('territories', 'territory', { code: 'EU' })
    await client.apply('territories', [{ action: 'insert', entity: 'territory', values: { code: 'X' } }])
    expect(seen.map((s) => s.url)).toEqual([
      '/apps/tv/_data/api/v1/models/territories/schema',
      '/apps/tv/_data/api/v1/models/territories/entities/territory/rows?limit=5&offset=10',
      '/apps/tv/_data/api/v1/models/territories/entities/territory/row?key.code=EU',
      '/apps/tv/_data/api/v1/models/territories/changes',
    ])
    expect(seen.every((s) => s.auth === 'Bearer tok')).toBe(true)
    expect(seen[3].method).toBe('POST')
  })

  it('reads every page up to the cap and says when it stopped short', async () => {
    const all = Array.from({ length: 1203 }, (_, i) => ({ values: { n: i }, version: 1, valid_from: null, valid_to: null }))
    const client = { rows: async (_m: string, _e: string, p: { limit: number; offset: number }) => ({ rows: all.slice(p.offset, p.offset + p.limit), limit: p.limit, offset: p.offset }) } as never
    expect((await loadAll(client, 'm', 'e')).rows).toHaveLength(1203)
    expect((await loadAll(client, 'm', 'e')).truncated).toBe(false)
    const capped = await loadAll(client, 'm', 'e', 1000)
    expect(capped.rows).toHaveLength(1000)
    expect(capped.truncated).toBe(true)
    expect((await loadAll(client, 'm', 'e', 1203)).truncated).toBe(false)
  })

  it('reaches the semantic client on the same route and token', async () => {
    const seen: { url: string; auth: string | null }[] = []
    const fake = (async (url: string, init?: RequestInit) => {
      seen.push({ url, auth: new Headers(init?.headers).get('Authorization') })
      return json({ status: 'ok', columns: [{ name: 'n', type: 'integer' }], rows: [[1]] })
    }) as unknown as typeof fetch
    const result = await appSemanticClient({ instance: 'tv', token: async () => 'tok' }, fake).query('sales', { measures: ['orders.n'], limit: 1 })
    expect(result.rows).toEqual([{ n: 1 }])
    expect(seen).toEqual([{ url: '/apps/tv/_data/api/v1/semantic/models/sales/query', auth: 'Bearer tok' }])
  })
})
