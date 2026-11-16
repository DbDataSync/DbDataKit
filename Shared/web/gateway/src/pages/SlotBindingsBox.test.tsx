import { describe, expect, it } from 'vitest'
import { mergeSecretSlots, refusedSlots, slotRows, slotsOfManifest } from './SlotBindingsBox'
import type { DataSourceSecretSlot, DefinitionDescription } from '../types'

describe('the slot bindings of a published model (plan 33 SW02)', () => {
  it('reads the slots a version names from its manifest, and none from a manifest that names none or is not JSON', () => {
    expect(slotsOfManifest('{"catalog":"x","slots":["by-hand","erp"]}')).toEqual(['by-hand', 'erp'])
    expect(slotsOfManifest('{"catalog":"x"}')).toEqual([])
    expect(slotsOfManifest('name: x')).toEqual([])
    expect(slotsOfManifest(null)).toEqual([])
  })
  it('lists each slot of the version a channel serves with its binding, who made it and when, and a slot nobody bound', () => {
    const description: DefinitionDescription = {
      kind: 'semantic-model', name: 'sales', latest_version: 3,
      channels: [{ channel: 'prod', active: 2, previous: 1, staged: 3 }, { channel: 'test', active: null, previous: null, staged: 3 }],
      bindings: [{ channel: 'prod', slot: 'by-hand', bound_to: 'by-hand', revision: 4, bound_by: 'admin:ada', bound_at: '2026-11-16T09:30:00Z' }],
    }
    const rows = slotRows(description, { 2: ['by-hand', 'erp'], 3: ['other'] })
    expect(rows).toEqual([
      { channel: 'prod', version: 2, slot: 'by-hand', boundTo: 'by-hand', boundBy: 'admin:ada', boundAt: '2026-11-16T09:30:00Z', revision: 4 },
      { channel: 'prod', version: 2, slot: 'erp', boundTo: null, boundBy: null, boundAt: null, revision: null },
    ])
  })
})

describe('the secret slots of a data source (plan 30 PB02)', () => {
  const slot = (name: string, bound: string | null): DataSourceSecretSlot => ({ slot: name, path: 'connection_string_secret', reference: `secret:${name}`, channel: 'prod', stored: true, held: true, bound_to: bound, bound_by: bound ? 'Ada' : null, bound_at: null, revision: bound ? 1 : null })
  it('reads the slots a refused write names only from a refusal for unbound slots', () => {
    expect(refusedSlots({ code: 'unboundslot', slots: [slot('dbdatakit:gateway:managed-x:erp', null)] }).map((s) => s.slot)).toEqual(['dbdatakit:gateway:managed-x:erp'])
    expect(refusedSlots({ error: 'not valid', diagnostics: [] })).toEqual([])
    expect(refusedSlots(undefined)).toEqual([])
  })
  it('shows the slots of the file served with their bindings, and adds the ones the refused text names', () => {
    const rows = mergeSecretSlots([slot('a', 'a')], [slot('a', null), slot('b', null)])
    expect(rows.map((r) => [r.slot, r.bound_to])).toEqual([['a', 'a'], ['b', null]])
    expect(mergeSecretSlots(undefined, undefined)).toEqual([])
  })
})
