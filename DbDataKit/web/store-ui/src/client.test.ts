import { describe, expect, it } from 'vitest'
import { createStoreClient, type Fetcher } from './client'
import { StoreError } from './types'

function recording(answer: (path: string, init?: RequestInit) => Response) {
  const calls: { path: string; init?: RequestInit }[] = []
  const fetcher: Fetcher = async (path, init) => { calls.push({ path, init }); return answer(path, init) }
  return { calls, client: createStoreClient(fetcher, '/relay/store/api/v1') }
}
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('the client', () => {
  it('asks for a page of rows, a row by its key, and its history', async () => {
    const { calls, client } = recording((p) => json(p.includes('history=true') ? { versions: [{ values: {}, version: 1, valid_from: null, valid_to: null }] } : p.includes('/rows') ? { rows: [], limit: 10, offset: 20 } : { values: { code: 'NA' }, version: 1, valid_from: null, valid_to: null }))

    expect((await client.rows('m', 'e', { limit: 10, offset: 20 })).offset).toBe(20)
    expect((await client.row('m', 'e', { code: 'N/A' }))!.version).toBe(1)
    expect(await client.history('m', 'e', { code: 'NA' })).toHaveLength(1)

    expect(calls.map((c) => c.path)).toEqual([
      '/relay/store/api/v1/models/m/entities/e/rows?limit=10&offset=20',
      '/relay/store/api/v1/models/m/entities/e/row?key.code=N%2FA',
      '/relay/store/api/v1/models/m/entities/e/row?key.code=NA&history=true',
    ])
  })

  it('a missing row is null, not an error', async () => {
    const { client } = recording(() => json({ error: 'no' }, 404))
    expect(await client.row('m', 'e', { code: 'x' })).toBeNull()
  })

  it('posts changes as JSON', async () => {
    const { calls, client } = recording(() => json({ applied: 1 }))
    await client.apply('m', [{ action: 'insert', entity: 'e', values: { a: 1 } }])
    expect(calls[0]!.init!.method).toBe('POST')
    expect(JSON.parse(calls[0]!.init!.body as string)).toEqual({ changes: [{ action: 'insert', entity: 'e', values: { a: 1 } }] })
  })

  it('a refusal carries the broken rules, a stale version the current one', async () => {
    const refused = recording(() => json({ error: 'breaks rules', violations: [{ rule: 'min', entity: 'e', attribute: 'a', key: {}, message: 'too low' }] }, 422))
    const error = await refused.client.apply('m', []).catch((e: unknown) => e)
    expect(error).toBeInstanceOf(StoreError)
    expect((error as StoreError).violations[0]!.attribute).toBe('a')

    const stale = recording(() => json({ error: 'stale', current_version: 7 }, 409))
    const conflict = (await stale.client.apply('m', []).catch((e: unknown) => e)) as StoreError
    expect([conflict.status, conflict.currentVersion]).toEqual([409, 7])
  })

  it('an answer that is not JSON still fails with the status', async () => {
    const { client } = recording(() => new Response('boom', { status: 500 }))
    await expect(client.schema('m')).rejects.toThrow('500')
  })
})
