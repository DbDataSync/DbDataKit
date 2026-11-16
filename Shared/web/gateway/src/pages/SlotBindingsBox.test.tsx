import { describe, expect, it } from 'vitest'
import { slotRows, slotsOfManifest } from './SlotBindingsBox'
import type { DefinitionDescription } from '../types'

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
