import { describe, expect, it } from 'vitest'
import { LOCK_PARAMETER, readFromTarget, surfaceAddress, thenWidgets } from './surface'

describe('the address a dialog or panel opens at', () => {
  it('is the route with the link\'s values as a sorted query and the locked names after them', () => {
    expect(surfaceAddress('/apps/sales/forms/orders', { region: 'East', id: 42 })).toBe('/apps/sales/forms/orders?id=42&region=East')
    expect(surfaceAddress('/apps/sales/forms/orders', { id: 42 }, ['status', 'customer id'])).toBe(`/apps/sales/forms/orders?id=42&${LOCK_PARAMETER}=status,customer%20id`)
    expect(surfaceAddress('/apps/sales/forms/orders', {})).toBe('/apps/sales/forms/orders')
    expect(surfaceAddress('/apps/sales/forms/orders', {}, ['status'])).toBe('/apps/sales/forms/orders?_lock=status')
    expect(surfaceAddress('/apps/sales/p?x=1', { a: 'b' })).toBe('/apps/sales/p?x=1&a=b')
  })
})

describe('what a target frame may say', () => {
  it('saved and close, and nothing else', () => {
    expect(readFromTarget({ type: 'views:saved', extra: 1 })).toEqual({ type: 'views:saved' })
    expect(readFromTarget({ type: 'views:close' })).toEqual({ type: 'views:close' })
    for (const bad of [null, 'views:saved', 1, {}, { type: 'views:open' }, { type: 'views:setParam' }]) expect(readFromTarget(bad)).toBeNull()
  })
})

describe('what resolves again when a surface closes', () => {
  const link = (thenKind: 'refresh' | 'none' | 'widgets', thenWidgets: string[] = []) => ({ thenKind, thenWidgets })
  it('after a save: the source widget for refresh, the named widgets for a list, nothing for none', () => {
    expect(thenWidgets(link('refresh'), 'orders', true)).toEqual(['orders'])
    expect(thenWidgets(link('widgets', ['a', 'b']), 'orders', true)).toEqual(['a', 'b'])
    expect(thenWidgets(link('none'), 'orders', true)).toEqual([])
  })
  it('a close without a save never refreshes', () => {
    for (const k of ['refresh', 'widgets', 'none'] as const) expect(thenWidgets(link(k, ['a']), 'orders', false)).toEqual([])
  })
})
