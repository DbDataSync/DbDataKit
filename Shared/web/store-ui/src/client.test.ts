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

  it('a write that was kept for approval says so (202), and one applied at once does not', async () => {
    const waiting = recording(() => json({ id: 'ab12cd34ef56ab12cd34ef56ab12cd34', state: 'pending' }, 202))
    expect(await waiting.client.apply('m', [{ action: 'insert', entity: 'e', values: { a: 1 } }])).toEqual({ waiting: { id: 'ab12cd34ef56ab12cd34ef56ab12cd34' } })
    const applied = recording(() => json({ applied: 1 }))
    expect(await applied.client.apply('m', [{ action: 'insert', entity: 'e', values: { a: 1 } }])).toEqual({})
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

describe('an instance of a model', () => {
  it('reads and writes under the instance, and asks for the list of instances without one', async () => {
    const { calls, client } = recording(() => json({ rows: [], limit: 25, offset: 0, mode: 'schema', instances: [] }))
    const acme = client.withInstance('acme')

    await acme.rows('m', 'e')
    await acme.row('m', 'e', { code: 'NA' })
    await acme.history('m', 'e', { code: 'NA' }).catch(() => undefined)
    await acme.apply('m', [])
    await acme.instances('m')
    await acme.schema('m')
    await client.withInstance('default').rows('m', 'e')
    await client.withInstance(null).rows('m', 'e')

    expect(calls.map((c) => c.path)).toEqual([
      '/relay/store/api/v1/models/m/instances/acme/entities/e/rows?limit=25&offset=0',
      '/relay/store/api/v1/models/m/instances/acme/entities/e/row?key.code=NA',
      '/relay/store/api/v1/models/m/instances/acme/entities/e/row?key.code=NA&history=true',
      '/relay/store/api/v1/models/m/instances/acme/changes',
      '/relay/store/api/v1/models/m/instances',
      '/relay/store/api/v1/models/m/instances/acme/schema',
      '/relay/store/api/v1/models/m/entities/e/rows?limit=25&offset=0',
      '/relay/store/api/v1/models/m/entities/e/rows?limit=25&offset=0',
    ])
  })
})

describe('a draft of the models (plan 21 CH01)', () => {
  it('reads and writes with the draft in the query, in an instance too, and the real data without one', async () => {
    const { calls, client } = recording(() => json({ rows: [], limit: 25, offset: 0, values: {}, version: 1, valid_from: null, valid_to: null, versions: [] }))
    const draft = client.withDraft('acme~preview')
    expect(draft.draftId).toBe('acme~preview')
    expect(client.draftId).toBeNull()

    await draft.rows('m', 'e')
    await draft.row('m', 'e', { code: 'NA' })
    await draft.apply('m', [])
    await draft.withInstance('acme').rows('m', 'e')
    await client.withInstance('acme').withDraft(null).rows('m', 'e')

    expect(calls.map((c) => c.path)).toEqual([
      '/relay/store/api/v1/models/m/entities/e/rows?limit=25&offset=0&draft=acme~preview',
      '/relay/store/api/v1/models/m/entities/e/row?key.code=NA&draft=acme~preview',
      '/relay/store/api/v1/models/m/changes?draft=acme~preview',
      '/relay/store/api/v1/models/m/instances/acme/entities/e/rows?limit=25&offset=0&draft=acme~preview',
      '/relay/store/api/v1/models/m/instances/acme/entities/e/rows?limit=25&offset=0',
    ])
  })

  it('reads the draft, discards it and submits it as a change request', async () => {
    const { calls, client } = recording((p, init) => {
      if (init?.method === 'DELETE') return json({ discarded: 1 })
      if (p.endsWith('/submit')) return json({ id: 'abc123', state: 'pending' }, 202)
      return p.endsWith('/drafts/p1') ? json({ id: 'p1', writes: [{ seq: 1, at: 'now', actor: 'ada', actor_name: null, applies: true, problem: null, changes: [] }] }) : json({ error: 'no' }, 404)
    })
    const draft = client.withDraft('p1')
    expect((await draft.draft('m'))!.writes).toHaveLength(1)
    expect(await client.withDraft('other').draft('m')).toBeNull()
    expect(await client.draft('m')).toBeNull()
    expect(await draft.submitDraft('m', 'from the preview')).toEqual({ id: 'abc123' })
    await draft.discardDraft('m')
    await expect(client.submitDraft('m')).rejects.toThrow('no draft')

    expect(calls.map((c) => `${c.init?.method ?? 'GET'} ${c.path}`)).toEqual([
      'GET /relay/store/api/v1/models/m/drafts/p1',
      'GET /relay/store/api/v1/models/m/drafts/other',
      'POST /relay/store/api/v1/models/m/drafts/p1/submit',
      'DELETE /relay/store/api/v1/models/m/drafts/p1',
    ])
    expect(JSON.parse(String(calls[2]!.init!.body))).toEqual({ description: 'from the preview' })
  })
})
