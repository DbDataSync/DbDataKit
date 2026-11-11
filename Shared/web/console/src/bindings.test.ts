import { describe, expect, it } from 'vitest'
import { connectionsFor, draftOf, draftProblem, isReleaseName, isSlotName, maxChoices, moveChoice, needsConfirmation, needsModel, orderedReleases, rowLine, runnerSlots, sameDraft, scopeOf, slotBody, unboundWords, type BindingConnection, type BindingRow, type BindingSlot } from './bindings'

const entry = (connection: string, model: string | null = null, usable = true) => ({ connection, model, usable, problem: usable ? null : 'no key', kind: null, target: null })
const row = (release: string, ...entries: ReturnType<typeof entry>[]): BindingRow => ({ release, wildcard: release === '*', entries, needsConfirmation: false, confirmed: false, effect: null })
const slot = (kind: BindingSlot['kind'], name: string, ...rows: BindingRow[]): BindingSlot => ({ section: kind === 'delivery' ? 'delivery' : 'models', kind, name, usedBy: [], limitPerMinute: null, rows })

describe('IX23: bindings, one shape for every slot', () => {
  it('reads a slot into a draft and writes it back as the server reads it: a model only for a model slot, empty release channels left out', () => {
    const chat = slot('model-chat', 'default', row('prod', entry('claude', 'claude-sonnet'), entry('local', 'llama3.1:8b')), row('test', entry('local', 'llama3.1:8b')))
    const draft = draftOf(chat)
    expect(draft.prod).toHaveLength(2)
    expect(slotBody('model-chat', { ...draft, dev: [] }).channels).toEqual({ prod: [{ connection: 'claude', model: 'claude-sonnet' }, { connection: 'local', model: 'llama3.1:8b' }], test: [{ connection: 'local', model: 'llama3.1:8b' }] })
    expect(slotBody('delivery', { prod: [{ connection: 'slack-ops', model: '' }] }, ['bindings/delivery/alerts/test'], 'why')).toEqual({ channels: { prod: [{ connection: 'slack-ops' }] }, confirm: ['bindings/delivery/alerts/test'], note: 'why' })
  })

  it('knows what each kind takes: a model for a model slot, a fallback list for chat only', () => {
    expect([needsModel('model-chat'), needsModel('model-embeddings'), needsModel('delivery'), needsModel('runner')]).toEqual([true, true, false, false])
    expect([maxChoices('model-chat') > 1, maxChoices('model-embeddings'), maxChoices('delivery')]).toEqual([true, 1, 1])
  })

  it('names what the screen can check before the server does', () => {
    expect(['alerts', 'a_b-2'].every(isSlotName)).toBe(true)
    expect(['', 'a b', '*'].some(isSlotName)).toBe(false)
    expect(['prod', 'qa-2', '*'].every(isReleaseName)).toBe(true)
    expect(['', 'a b', '**'].some(isReleaseName)).toBe(false)
    expect(draftProblem('delivery', { prod: [{ connection: 'a', model: '' }] })).toBeNull()
    expect(draftProblem('delivery', { 'te st': [{ connection: 'a', model: '' }] })).toContain('not a release channel name')
    expect(draftProblem('delivery', { prod: [{ connection: 'a', model: '' }, { connection: 'b', model: '' }] })).toContain('one connection')
    expect(draftProblem('model-chat', { prod: [{ connection: 'a', model: '' }] })).toContain('Choose a model')
    expect(draftProblem('model-chat', { prod: [{ connection: '', model: '' }] })).toContain('Choose a connection')
    expect(draftProblem('model-embeddings', { prod: [{ connection: 'a', model: 'm' }, { connection: 'b', model: 'n' }] })).toContain('one connection')
  })

  it('knows when there is nothing to save and orders release channels prod first and * last', () => {
    const a = { prod: [{ connection: 'a', model: '' }], test: [{ connection: 'b', model: '' }] }
    expect(sameDraft(a, { test: [{ connection: 'b', model: '' }], prod: [{ connection: 'a', model: '' }] })).toBe(true)
    expect(sameDraft(a, { ...a, test: [{ connection: 'c', model: '' }] })).toBe(false)
    expect(orderedReleases(['*', 'test', 'prod', 'dev'])).toEqual(['prod', 'dev', 'test', '*'])
  })

  it('reorders a fallback one place and leaves an out-of-range move alone', () => {
    expect(moveChoice(['a', 'b', 'c'], 2, -1)).toEqual(['a', 'c', 'b'])
    expect(moveChoice(['a', 'b', 'c'], 0, -1)).toEqual(['a', 'b', 'c'])
    expect(moveChoice(['a', 'b', 'c'], 2, 1)).toEqual(['a', 'b', 'c'])
  })

  it('offers a slot only the connections that fit it', () => {
    const c = (name: string, kind: string, purpose: string | null, canSend = false): BindingConnection => ({ name, kind, purpose, models: [], canSend, target: null, cloud: null })
    const all = [c('claude', 'model-provider', 'chat'), c('embed', 'model-provider', 'embeddings'), c('mail', 'smtp', null, true), c('crm', 'http', null), c('db', 'database', null)]
    expect(connectionsFor('model-chat', all).map((x) => x.name)).toEqual(['claude'])
    expect(connectionsFor('model-embeddings', all).map((x) => x.name)).toEqual(['embed'])
    expect(connectionsFor('delivery', all).map((x) => x.name)).toEqual(['mail'])
  })

  it('asks for the confirmation of production\'s connection in a non-production release channel of a delivery channel only (IX17)', () => {
    const delivery = slot('delivery', 'alerts')
    expect(needsConfirmation(delivery, { prod: [{ connection: 'ops', model: '' }], test: [{ connection: 'sandbox', model: '' }] })).toEqual([])
    expect(needsConfirmation(delivery, { prod: [{ connection: 'ops', model: '' }], test: [{ connection: 'ops', model: '' }], dev: [{ connection: 'sandbox', model: '' }] }))
      .toEqual([{ scope: scopeOf('delivery', 'alerts', 'test'), release: 'test', connection: 'ops' }])
    expect(needsConfirmation(delivery, { '*': [{ connection: 'ops', model: '' }] })).toEqual([{ scope: 'bindings/delivery/alerts/*', release: '*', connection: 'ops' }])   // only `*`: production's connection serves every other release channel
    expect(needsConfirmation(slot('model-chat', 'default'), { prod: [{ connection: 'a', model: 'm' }], test: [{ connection: 'a', model: 'm' }] })).toEqual([])           // a model only reads
  })

  it('says in words what a row uses and why a release channel has nothing', () => {
    expect(rowLine(row('prod', entry('claude', 'sonnet'), entry('local', 'llama', false)))).toBe('claude/sonnet then local/llama (cannot be used)')
    const alerts = slot('delivery', 'alerts', row('prod', entry('ops')), row('test', entry('sandbox')))
    expect(unboundWords(alerts, 'dev')).toBe("Delivery channel 'alerts' is not bound in dev (bound in prod, test); it never uses another release channel's connection.")
    expect(unboundWords(slot('delivery', 'x'), 'prod')).toContain('bound nowhere')
  })

  it('shows the runner\'s package slots in the same shape: per release channel one connection, none inherited', () => {
    const slots = runnerSlots('console-runner', 'orders', {
      slots: [{ channel: 'dev', slot: 'source', kind: 'sqlserver', tasks: ['load'], versions: [1] }, { channel: 'prod', slot: 'source', kind: 'sqlserver', tasks: ['load'], versions: [1] }, { channel: 'dev', slot: 'target', kind: 'postgres', tasks: ['load'], versions: [1] }],
      bindings: [{ channel: 'dev', slot: 'source', bound_to: 'erp', revision: 1 }],
    })
    expect(slots.map((s) => s.name)).toEqual(['orders/source', 'orders/target'])
    const source = slots[0]
    expect(source.kind).toBe('runner')
    expect(source.runner?.package).toBe('orders')
    expect(source.rows.find((r) => r.release === 'dev')?.entries.map((e) => e.connection)).toEqual(['erp'])
    expect(source.rows.find((r) => r.release === 'prod')?.entries).toEqual([])        // prod is not bound: it does not take dev's
    expect(slots[1].rows[0].entries).toEqual([])
  })
})
