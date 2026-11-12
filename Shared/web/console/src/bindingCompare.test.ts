import { describe, expect, it } from 'vitest'
import { adoptWords, decisionOf, differing, heldWords, itemTitle, publishWords, summary, type CompareItem, type PostureEntry } from './bindingCompare'

const item = (over: Partial<CompareItem> = {}): CompareItem => ({
  id: 'binding/agent/helper/model:default/test', family: 'binding', name: 'helper', definitionKind: 'agent', slot: 'model:default', channel: 'test', state: 'differs',
  here: 'local/llama3.1:8b', there: 'claude/claude-sonnet-4-5', hereText: null, thereText: null, canAdopt: true, canPublish: true, problem: null, ...over,
})

describe('IX25: compare, adopt and publish', () => {
  it('asks for an item by what it is and by the other side value it saw, so the server can refuse a decision taken on an older value', () => {
    expect(decisionOf('gateway-1', item())).toEqual({ server: 'gateway-1', family: 'binding', name: 'helper', definitionKind: 'agent', slot: 'model:default', channel: 'test', expected: 'claude/claude-sonnet-4-5' })
    expect(decisionOf('console-runner', item({ family: 'connection', name: 'crm', definitionKind: null, slot: null, channel: null, there: 'sha256:abc' })).expected).toBe('sha256:abc')
  })

  it('names an item as a person would: a binding by its definition, slot and release channel, a connection by its name', () => {
    expect(itemTitle(item())).toBe('agent helper: model:default in test')
    expect(itemTitle(item({ family: 'connection', name: 'crm', definitionKind: null, slot: null, channel: null }))).toBe('crm')
  })

  it('says what each button does, including that publishing the console having none empties the slot there', () => {
    expect(adoptWords(item(), 'Gateway')).toBe("Use Gateway's value in the console")
    expect(adoptWords(item({ family: 'connection' }), 'the console\'s runner')).toContain('as the console\'s file')
    expect(publishWords(item(), 'Gateway')).toBe("Send the console's value to Gateway")
    expect(publishWords(item({ here: null, state: 'only-there' }), 'Gateway')).toBe('Empty it on Gateway')
  })

  it('puts what differs first and counts every state', () => {
    const items = [item({ state: 'same' }), item({ state: 'differs' }), item({ state: 'only-here' }), item({ state: 'only-there' }), item({ state: 'same' })]
    expect(differing(items).map((i) => i.state)).toEqual(['differs', 'only-here', 'only-there'])
    expect(summary(items)).toBe('1 differ, 1 only in the console, 1 only on the other server, 2 the same')
    expect(summary([])).toBe('Nothing to compare.')
  })

  it('says where a posture choice is held, once however many tables hold it', () => {
    const e = (heldBy: string[]): PostureEntry => ({ setting: 's', scope: 'x', chosenBy: 'a', chosenAt: '2026-11-12T09:00:00Z', note: null, heldBy, default: null, effect: null })
    expect(heldWords(e(['console']))).toBe('held by console')
    expect(heldWords(e(['console', 'the console\'s runner']))).toBe('held by console and the console\'s runner')
  })
})
