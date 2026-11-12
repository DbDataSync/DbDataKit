import { describe, expect, it } from 'vitest'
import { actionsFor, draftBlock, draftBody, emptyItem, itemLine, type ReleaseItemView } from './releases'

const item = { ...emptyItem, target: 'runner-prod', source: 'runner-test', name: 'orders', version: '4', fromChannel: 'test', toChannel: 'prod' }

describe('the buttons of a release', () => {
  it('offers each move only in the state that allows it', () => {
    expect(actionsFor('draft')).toEqual({ approve: true, start: false, resume: false, abandon: true })
    expect(actionsFor('approved')).toEqual({ approve: false, start: true, resume: false, abandon: true })
    expect(actionsFor('stopped')).toEqual({ approve: false, start: false, resume: true, abandon: true })
    expect(actionsFor('running')).toEqual({ approve: false, start: false, resume: false, abandon: false })
    expect(actionsFor('succeeded').abandon).toBe(false)
  })
})

describe('the draft', () => {
  it('says what is missing, naming the item', () => {
    expect(draftBlock('', [item])).toBe('Name the release.')
    expect(draftBlock('r', [])).toBe('Add at least one item.')
    expect(draftBlock('r', [{ ...item, target: '' }])).toBe('Item 1: choose the runner it goes to.')
    expect(draftBlock('r', [item, { ...item, version: '0' }])).toBe('Item 2: the version is a whole number.')
    expect(draftBlock('r', [{ ...item, toChannel: ' ' }])).toBe('Item 1: name both channels.')
    expect(draftBlock('r', [item])).toBeNull()
  })
  it('sends the source only when one is chosen', () => {
    expect(draftBody(' r ', [item, { ...item, source: '' }])).toEqual({
      name: 'r',
      items: [{ target: 'runner-prod', source: 'runner-test', name: 'orders', version: 4, fromChannel: 'test', toChannel: 'prod' }, { target: 'runner-prod', name: 'orders', version: 4, fromChannel: 'test', toChannel: 'prod' }],
    })
  })
})

describe('an item in words', () => {
  const view: ReleaseItemView = { position: 1, product: 'runner', target: 'runner-prod', source: 'runner-test', kind: 'integration', name: 'orders', version: 4, fromChannel: 'test', toChannel: 'prod', state: 'pending', error: null, finishedAt: null }
  const names = (id: string) => ({ 'runner-prod': 'Prod runner', 'runner-test': 'Test runner' })[id] ?? id
  it('names both servers and the channels', () => {
    expect(itemLine(view, names)).toBe('orders v4 from Test runner to Prod runner: test to prod')
    expect(itemLine({ ...view, source: null }, names)).toBe('orders v4 to Prod runner: test to prod')
  })
})
