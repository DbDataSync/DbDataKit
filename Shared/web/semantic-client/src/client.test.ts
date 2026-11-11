import { describe, expect, it } from 'vitest'
import { SemanticError, createSemanticClient, type Fetcher } from './index'

/** A fake host: records what the client sent and answers from a table of routes, as the real host's semantic endpoint will. */
function fakeHost(answer: (method: string, path: string, body: any) => Response) {
  const calls: { method: string; path: string; body: any; headers: Headers }[] = []
  const fetcher: Fetcher = async (path, init) => {
    const body = init?.body ? JSON.parse(String(init.body)) : undefined
    calls.push({ method: init?.method ?? 'GET', path, body, headers: new Headers(init?.headers) })
    return answer(init?.method ?? 'GET', path, body)
  }
  return { calls, client: createSemanticClient(fetcher, '/apps/sales/_data/api/v1') }
}
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('the semantic client', () => {
  it('sends a request in the envelope of the query document and reads rows by column name', async () => {
    const { calls, client } = fakeHost(() => json({ status: 'ok', columns: [{ name: 'region', type: 'string' }, { name: 'revenue', type: 'number' }], rows: [['East', 10.5], ['West', null]], warnings: ['rounded'], request_hash: 'abc' }))

    const result = await client.query('sales', { measures: ['orders.net_revenue'], attributes: ['customers.region'], order: [['orders.net_revenue', 'desc']], limit: 5 })

    expect(calls[0]).toMatchObject({ method: 'POST', path: '/apps/sales/_data/api/v1/semantic/models/sales/query' })
    expect(calls[0].body).toEqual({ spec_version: '0.3', kind: 'query', query: { measures: ['orders.net_revenue'], attributes: ['customers.region'], order: [['orders.net_revenue', 'desc']], limit: 5 } })
    expect(calls[0].headers.get('content-type')).toBe('application/json')
    expect(result.rows).toEqual([{ region: 'East', revenue: 10.5 }, { region: 'West', revenue: null }])
    expect(result).toMatchObject({ status: 'ok', warnings: ['rounded'], requestHash: 'abc' })
  })

  it('accepts rows that are already objects', async () => {
    const { client } = fakeHost(() => json({ columns: [{ name: 'n', type: 'integer' }], rows: [{ n: 3 }] }))
    expect((await client.query('m', { measures: ['x'], limit: 1 })).rows).toEqual([{ n: 3 }])
  })

  it('refuses a request without a limit, without a measure or with a limit out of range before it asks the host', async () => {
    const { calls, client } = fakeHost(() => json({}))
    await expect(client.query('m', { measures: ['x'], limit: 0 })).rejects.toMatchObject({ code: 'invalid' })
    await expect(client.query('m', { measures: ['x'], limit: 10_001 })).rejects.toThrow(/limit from 1 to 10,000/)
    await expect(client.query('m', { measures: [], limit: 5 })).rejects.toThrow(/at least one measure/)
    expect(calls).toHaveLength(0)
  })

  it('lists models and reads a catalog, naming the model in the path', async () => {
    const { calls, client } = fakeHost((_m, path) => json(path.endsWith('/models') ? { models: [{ name: 'a/b', title: 'A', version: '3' }] } : { model: 'a/b', measures: [{ name: 'orders.n', title: null, description: null, type: 'integer' }], attributes: [] }))
    expect((await client.models())[0].name).toBe('a/b')
    expect((await client.catalog('a/b')).measures[0].name).toBe('orders.n')
    expect(calls.map((c) => c.path)).toEqual(['/apps/sales/_data/api/v1/semantic/models', '/apps/sales/_data/api/v1/semantic/models/a%2Fb/catalog'])
  })

  it('turns a status into an error with the server\'s words', async () => {
    const { client } = fakeHost((_m, path) => path.includes('/none/') ? json({ message: 'No model none.' }, 404) : path.includes('/locked/') ? json({ message: 'no' }, 403) : path.includes('/bad/') ? json({ message: 'unknown measure x' }, 422) : json({}, 502))
    await expect(client.catalog('none')).rejects.toMatchObject({ code: 'not_found', message: 'No model none.', status: 404 })
    await expect(client.catalog('locked')).rejects.toMatchObject({ code: 'denied' })
    await expect(client.query('bad', { measures: ['x'], limit: 1 })).rejects.toMatchObject({ code: 'invalid', message: 'unknown measure x' })
    await expect(client.query('m', { measures: ['x'], limit: 1 })).rejects.toMatchObject({ code: 'unavailable', status: 502 })
  })

  it('reports a question the engine asks back (clarify) or will not run (unsupported) as an error with its reason', async () => {
    const { client } = fakeHost(() => json({ status: 'clarify', reason: 'Which region?' }))
    const error = await client.query('m', { measures: ['x'], limit: 1 }).catch((e) => e)
    expect(error).toBeInstanceOf(SemanticError)
    expect(error).toMatchObject({ code: 'clarify', message: 'Which region?' })
  })

  it('says so when the host cannot be reached, and rethrows an abort as it is', async () => {
    const client = createSemanticClient(async () => { throw new TypeError('network down') })
    await expect(client.models()).rejects.toMatchObject({ code: 'unavailable', message: expect.stringContaining('network down') })
    const controller = new AbortController(); controller.abort()
    const aborting = createSemanticClient(async () => { throw new DOMException('aborted', 'AbortError') })
    await expect(aborting.query('m', { measures: ['x'], limit: 1 }, { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' })
  })

  it('refuses an answer without rows', async () => {
    const { client } = fakeHost(() => json({ status: 'ok', columns: [] }))
    await expect(client.query('m', { measures: ['x'], limit: 1 })).rejects.toMatchObject({ code: 'bad_response' })
  })
})
