import { describe, expect, it } from 'vitest'
import { emptyPromoteForm, hostedServer, parseBindings, promoteBlock, promoteBody, promotionText, targetServers, type PromoteAnswer } from './promote'

const form = { ...emptyPromoteForm, to: 'runner-2', fromChannel: 'test', toChannel: 'prod' }

describe('where the screens are hosted', () => {
  it('knows the server a console relays to, and nothing when the app is its own', () => {
    expect(hostedServer('/relay/runner-1')).toBe('runner-1')
    expect(hostedServer('/relay/console-runner/')).toBe('console-runner')
    expect(hostedServer('')).toBeNull()
    expect(hostedServer('/relay/a/b')).toBeNull()
  })
  it('offers the other runners that answer, not this one nor another product', () => {
    const all = [
      { id: 'runner-1', name: 'Test', product: 'dbdatarunner', reachable: true }, { id: 'runner-2', name: 'Prod', product: 'dbdatarunner', reachable: true },
      { id: 'runner-3', name: 'Down', product: 'dbdatarunner', reachable: false }, { id: 'gw', name: 'Gateway', product: 'dbdatagateway', reachable: true },
    ]
    expect(targetServers(all, 'runner-1').map((s) => s.id)).toEqual(['runner-2'])
  })
})

describe('the connections of the target channel', () => {
  it('reads slot=connection lines and skips blank ones', () => {
    expect(parseBindings('source = erp-prod\n\n  sink=warehouse ')).toEqual({ ok: true, slots: { source: 'erp-prod', sink: 'warehouse' } })
    expect(parseBindings('')).toEqual({ ok: true, slots: {} })
  })
  it('names a line that is not one', () => {
    expect(parseBindings('source')).toEqual({ ok: false, problem: '"source" is not slot=connection.' })
    expect(parseBindings('=erp')).toMatchObject({ ok: false })
    expect(parseBindings('source=')).toMatchObject({ ok: false })
  })
})

describe('the form', () => {
  it('says what is missing, in order', () => {
    expect(promoteBlock(emptyPromoteForm)).toBe('Choose the runner it goes to.')
    expect(promoteBlock({ ...form, fromChannel: ' ' })).toBe('Name the channel it is read from.')
    expect(promoteBlock({ ...form, toChannel: '' })).toBe('Name the channel it goes to.')
    expect(promoteBlock({ ...form, version: '1.5' })).toBe('A version is a whole number.')
    expect(promoteBlock({ ...form, bindings: 'x' })).toBe('"x" is not slot=connection.')
    expect(promoteBlock(form)).toBeNull()
  })
  it('sends only what was chosen', () => {
    expect(promoteBody('runner-1', 'orders', form)).toEqual({ from: 'runner-1', to: 'runner-2', name: 'orders', fromChannel: 'test', toChannel: 'prod' })
    expect(promoteBody('runner-1', 'orders', { ...form, version: '3', mode: 'stage', bindings: 'source=erp-prod' }))
      .toEqual({ from: 'runner-1', to: 'runner-2', name: 'orders', fromChannel: 'test', toChannel: 'prod', version: 3, mode: 'stage', bindings: { source: 'erp-prod' } })
  })
})

describe('what a promotion says', () => {
  const answer: PromoteAnswer = { kind: 'integration', name: 'orders', fromChannel: 'test', toChannel: 'prod', sourceVersion: 4, targetVersion: 2, changed: true, mode: 'stage', signature: 'unsigned', sent: 3, skipped: 1, bound: [] }
  it('counts the files and names the version each group holds', () => {
    expect(promotionText(answer, 'Prod')).toBe('Version 4 of orders: Prod now holds version 2 in prod (stage). 3 files sent, 1 already there.')
  })
  it('says a second promotion sent nothing and changed nothing', () => {
    expect(promotionText({ ...answer, changed: false, sent: 0, skipped: 4 }, 'Prod')).toBe('Version 4 of orders: Prod already held this content: version 2 in prod, nothing changed. No file was sent (the target had them all).')
  })
})
