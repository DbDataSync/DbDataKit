import { describe, expect, it } from 'vitest'
import type { AttributeMapping } from './api'
import { checkMapping, mappingRequest, ordered, sentence, summarise } from './attributeMappings'

const east: AttributeMapping = { id: 'a', source: 'entra', group: 'g-east', attribute: 'region', value: 'east' }
const west: AttributeMapping = { id: 'b', source: 'entra', group: 'g-west', attribute: 'region', value: 'west' }
const form = { source: 'entra', group: 'g-north', attribute: 'region', value: 'north' }

describe('the attribute mapping form', () => {
  it('is ready when a group, an attribute and a value are given', () => {
    expect(checkMapping(form, [east])).toEqual({ problems: {}, ok: true, why: null })
  })

  it('says what is missing, one thing at a time, before anything is sent', () => {
    expect(checkMapping({ ...form, group: ' ' }, []).why).toBe('Name the external group.')
    expect(checkMapping({ ...form, attribute: '' }, []).why).toBe('Name the attribute.')
    expect(checkMapping({ ...form, value: '' }, []).why).toBe('Give the value the group gives.')
  })

  it('refuses an attribute name the identity home would refuse, in the rule\'s words', () => {
    for (const bad of ['Region', '1st', 'has space', 'a-b', 'x'.repeat(65)]) {
      const c = checkMapping({ ...form, attribute: bad }, [])
      expect(c.ok, bad).toBe(false)
      expect(c.problems.attribute, bad).toContain('lower case letters, digits and underscores')
    }
    for (const good of ['region', 'cost_centre2', 'a', 'x'.repeat(64)]) expect(checkMapping({ ...form, attribute: good }, []).ok, good).toBe(true)
  })

  it('refuses a value over 200 characters and a group that already gives the value', () => {
    expect(checkMapping({ ...form, value: 'v'.repeat(201) }, []).problems.value).toContain('200')
    expect(checkMapping({ ...form, value: 'v'.repeat(200) }, []).ok).toBe(true)
    const again = checkMapping({ ...form, group: 'g-east', value: 'east' }, [east])
    expect(again.ok).toBe(false)
    expect(again.why).toContain('already gives')
    // the same group in the other directory is another group
    expect(checkMapping({ ...form, source: 'windows', group: 'g-east', value: 'east' }, [east]).ok).toBe(true)
  })

  it('sends the trimmed fields', () => {
    expect(mappingRequest({ source: 'windows', group: ' CORP\\East ', attribute: ' region ', value: ' east ' })).toEqual({ source: 'windows', group: 'CORP\\East', attribute: 'region', value: 'east' })
  })
})

describe('what the mappings give', () => {
  it('lists several values of one attribute together, attributes in order', () => {
    const owner: AttributeMapping = { id: 'c', source: 'windows', group: 'CORP\\Own', attribute: 'cost_centre', value: '7' }
    expect(summarise([west, owner, east, { ...east, id: 'd', group: 'g-east2' }])).toEqual([
      { attribute: 'cost_centre', values: ['7'] },
      { attribute: 'region', values: ['east', 'west'] },
    ])
  })

  it('keeps a steady order and says one mapping in a sentence', () => {
    expect(ordered([west, east]).map((m) => m.id)).toEqual(['a', 'b'])
    expect(sentence(east)).toBe('Members of the entra group g-east have region = east')
  })
})
