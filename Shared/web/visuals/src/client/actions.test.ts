import { describe, expect, it } from 'vitest'
import type { ActionRequest } from '../links/actions'
import { clientActionHost } from '../links/actions'
import { fixtureClient } from './fixtureClient'
import { actionOutcome, batchOutcome, moduleClient } from './moduleClient'

const request: ActionRequest = { link: 'close-order', widget: 'orders', row: { 'orders.id': 1 }, parameters: { region: 'EMEA' }, answers: { status: 'Held', note: undefined }, idempotencyKey: 'k-1' }

describe('the module answers an action as the popup\'s outcome', () => {
  it('maps each status of the route', () => {
    expect(actionOutcome(200, { status: 'applied' })).toEqual({ status: 'applied' })
    expect(actionOutcome(202, { status: 'waiting', request_id: 'cr-1' })).toEqual({ status: 'waiting', requestId: 'cr-1' })
    expect(actionOutcome(202, { status: 'started', run_id: 'r-1' })).toEqual({ status: 'started', runId: 'r-1' })
    expect(actionOutcome(409, { status: 'conflict', message: 'moved' })).toEqual({ status: 'conflict', message: 'moved' })
    expect(actionOutcome(422, { status: 'refused', message: 'rules', violations: [{ rule: 'allowed', attribute: 'status', message: 'no' }] })).toEqual({ status: 'refused', message: 'rules', violations: [{ rule: 'allowed', attribute: 'status', message: 'no' }] })
    expect(actionOutcome(403, { status: 'denied', message: 'author role' })).toEqual({ status: 'denied', message: 'author role' })
    expect(actionOutcome(403, { code: 'action_not_allowed', message: 'app scope' })).toEqual({ status: 'denied', message: 'app scope' })
    expect(actionOutcome(400, { code: 'missing_values', message: 'the row key' })).toEqual({ status: 'refused', message: 'the row key' })
    expect(actionOutcome(501, { code: 'actions_unavailable', message: 'no store' })).toEqual({ status: 'failed', message: 'no store' })
    expect(actionOutcome(0, null).status).toBe('failed')
  })

  it('posts the link, the row and the key to the document\'s action route and reads the status whatever it is', async () => {
    const sent: { url: string; body: unknown }[] = []
    const answers = [{ status: 409, body: { status: 'conflict', message: 'moved' } }, { status: 200, body: { will: 'wait', note: 'by the owner' } }]
    const fetcher = (async (url: string, init: RequestInit) => { sent.push({ url, body: JSON.parse(init.body as string) }); const a = answers.shift()!; return new Response(JSON.stringify(a.body), { status: a.status }) }) as unknown as typeof fetch
    const client = moduleClient({ baseUrl: 'https://views.example/', fetch: fetcher })
    expect(await client.runAction!({ kind: 'dashboard', name: 'sales' }, request)).toEqual({ status: 'conflict', message: 'moved' })
    expect(sent[0]).toEqual({ url: 'https://views.example/api/v1/dashboards/sales/actions/close-order', body: { widget: 'orders', row: { 'orders.id': 1 }, parameters: { region: 'EMEA' }, answers: { status: 'Held' }, idempotency_key: 'k-1' } })
    expect(await client.forecastAction!({ kind: 'dashboard', name: 'sales' }, 'close-order')).toEqual({ will: 'wait', note: 'by the owner' })
    expect(sent[1]!.url).toBe('https://views.example/api/v1/dashboards/sales/actions/close-order/forecast')
  })

  it('a network failure is a failure to show, not a thrown error', async () => {
    const client = moduleClient({ baseUrl: 'https://x', fetch: (async () => { throw new Error('offline') }) as unknown as typeof fetch })
    expect((await client.runAction!({ kind: 'dashboard', name: 'sales' }, request)).status).toBe('failed')
  })
})

describe('the fixture source runs the actions a bundle records', () => {
  const bundle = { documents: {}, actions: { 'dashboard/sales': { 'close-order': { forecast: { will: 'apply' as const }, outcome: (_r: ActionRequest, calls: number) => (calls === 1 ? { status: 'conflict' as const, message: 'moved' } : { status: 'applied' as const }) } } } }
  it('answers by the recorded outcome and keeps a finished call by its key', async () => {
    const host = clientActionHost(fixtureClient(bundle), { kind: 'dashboard', name: 'sales' })!
    expect(await host.forecast!('close-order')).toEqual({ will: 'apply' })
    expect((await host.run(request)).status).toBe('conflict')
    expect((await host.run(request)).status).toBe('applied')
    expect((await host.run(request)).status).toBe('applied')       // the same key: the same answer, the function is not called again
    expect((await host.run({ ...request, link: 'nothing' })).status).toBe('failed')
  })
  it('a client with no actions gives no host', () => {
    expect(clientActionHost({}, { kind: 'dashboard', name: 'x' })).toBeUndefined()
  })
})

describe('the module answers a batch as the review\'s outcome', () => {
  it('maps each status and carries the problems by line', () => {
    expect(batchOutcome(200, { status: 'applied', count: 30 })).toEqual({ status: 'applied', count: 30 })
    expect(batchOutcome(202, { status: 'waiting', count: 30, request_id: 'cr-1' })).toEqual({ status: 'waiting', count: 30, requestId: 'cr-1' })
    expect(batchOutcome(422, { status: 'refused', message: 'rules', problems: [{ line: 4, rule: 'max', attribute: 'units', message: 'too many' }, { line: null, message: 'set' }] }))
      .toEqual({ status: 'refused', message: 'rules', problems: [{ line: 4, rule: 'max', attribute: 'units', message: 'too many' }, { line: null, message: 'set' }] })
    expect(batchOutcome(409, { status: 'conflict', message: 'moved', problems: [{ line: 0, message: 'v' }] }).status).toBe('conflict')
    expect(batchOutcome(400, { code: 'two_models', message: 'one model' })).toEqual({ status: 'refused', message: 'one model', problems: [] })
    expect(batchOutcome(403, { code: 'action_not_allowed', message: 'scope' }).status).toBe('denied')
    expect(batchOutcome(0, null).status).toBe('failed')
  })

  it('posts the lines and one key to the batch route and asks the forecast with the same lines', async () => {
    const sent: { url: string; body: any }[] = []
    const answers = [{ status: 422, body: { status: 'refused', message: 'rules', problems: [{ line: 1, message: 'no' }] } }, { status: 200, body: { will: 'wait', note: 'one request' } }]
    const fetcher = (async (url: string, init: RequestInit) => { sent.push({ url, body: JSON.parse(init.body as string) }); const a = answers.shift()!; return new Response(JSON.stringify(a.body), { status: a.status }) }) as unknown as typeof fetch
    const client = moduleClient({ baseUrl: 'https://views.example', fetch: fetcher })
    const batch = { lines: [request, { ...request, row: { 'orders.id': 2 } }].map(({ link, widget, row, parameters, answers }) => ({ link, widget, row, parameters, answers })), idempotencyKey: 'b-1' }
    const outcome = await client.runBatch!({ kind: 'dashboard', name: 'sales' }, batch)
    expect(outcome).toEqual({ status: 'refused', message: 'rules', problems: [{ line: 1, message: 'no' }] })
    expect(sent[0]!.url).toBe('https://views.example/api/v1/dashboards/sales/batches')
    expect(sent[0]!.body.idempotency_key).toBe('b-1')
    expect(sent[0]!.body.lines).toHaveLength(2)
    expect(sent[0]!.body.lines[0]).toEqual({ link: 'close-order', widget: 'orders', row: { 'orders.id': 1 }, parameters: { region: 'EMEA' }, answers: { status: 'Held' } })
    expect(await client.forecastBatch!({ kind: 'dashboard', name: 'sales' }, batch)).toEqual({ will: 'wait', note: 'one request' })
    expect(sent[1]!.url).toBe('https://views.example/api/v1/dashboards/sales/batches/forecast')
  })

  it('the fixture source answers a recorded batch and keeps a finished one by its key', async () => {
    const calls: number[] = []
    const host = clientActionHost(fixtureClient({ documents: {}, batches: { 'dashboard/sales': { forecast: { will: 'apply' }, outcome: (r) => { calls.push(r.lines.length); return { status: 'applied', count: r.lines.length } } } } }), { kind: 'dashboard', name: 'sales' })!
    expect(await host.forecastBatch!({ lines: [], idempotencyKey: 'k' })).toEqual({ will: 'apply' })
    expect(await host.submit!({ lines: [request], idempotencyKey: 'k' })).toEqual({ status: 'applied', count: 1 })
    expect(await host.submit!({ lines: [request, request], idempotencyKey: 'k' })).toEqual({ status: 'applied', count: 1 })
    expect(calls).toEqual([1])
  })
})
