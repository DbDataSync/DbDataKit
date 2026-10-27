import { describe, expect, it } from 'vitest'
import { SchemaSet } from './schemaSet'

const config = { $id: 'config.schema.json', $schema: 'https://json-schema.org/draft/2020-12/schema', type: 'object', properties: { defaults: { description: 'Model settings.', type: 'object', properties: { tags: { type: 'array', items: { $ref: '#/$defs/tag' } } } } }, $defs: { tag: { type: 'string', minLength: 1 } } }
const folder = { $id: 'folder.schema.json', $schema: 'https://json-schema.org/draft/2020-12/schema', type: 'object', additionalProperties: false, properties: { defaults: { $ref: 'config.schema.json#/properties/defaults' } } }

const set = () => new SchemaSet({ 'config.schema.json': config as never, 'folder.schema.json': folder as never })

describe('a set of schemas that refer to each other', () => {
  it('follows a reference into another file and keeps the referrer in its own file for the rest', () => {
    const { node, loc } = set().at({ base: 'folder.schema.json', pointer: '/properties/defaults' })
    expect(loc).toEqual({ base: 'config.schema.json', pointer: '/properties/defaults' })
    expect(node.description).toBe('Model settings.')
    expect(set().at(set().child(loc, 'properties', 'tags', 'items')).loc).toEqual({ base: 'config.schema.json', pointer: '/$defs/tag' })
  })

  it('says whether a value fits a place, across files', () => {
    const s = set()
    const place = { base: 'folder.schema.json', pointer: '/properties/defaults' }
    expect(s.fits(place, { tags: ['a'] })).toBe(true)
    expect(s.fits(place, { tags: [''] })).toBe(false)
    expect(s.fits(place, { tags: 'a' })).toBe(false)
  })

  it('reports the problems of a document with the path of each', () => {
    const problems = set().problems('folder.schema.json', { defaults: { tags: ['a', 5] }, extra: 1 })
    expect(problems).toContainEqual({ path: ['defaults', 'tags', 1], message: 'Must be string.' })
    expect(problems).toContainEqual({ path: [], message: '`extra` is not a setting here.' })
  })

  it('refuses a place that is not there, naming it', () => {
    expect(() => set().at({ base: 'folder.schema.json', pointer: '/properties/nothing' })).toThrow(/Nothing at folder.schema.json#\/properties\/nothing/)
    expect(() => set().at({ base: 'missing.json', pointer: '' })).toThrow(/no schema 'missing.json'/)
  })
})

describe('what is said about a value that fits none of several shapes', () => {
  const shapes = {
    $id: 'shapes.schema.json', $schema: 'https://json-schema.org/draft/2020-12/schema',
    type: 'object',
    properties: {
      load: {
        oneOf: [
          { type: 'object', additionalProperties: false, required: ['strategy'], properties: { strategy: { const: 'full' } } },
          { type: 'object', additionalProperties: false, required: ['strategy'], properties: { strategy: { const: 'range' }, column: { type: 'string' }, days: { type: 'integer' } } },
        ],
      },
    },
  }

  it('keeps only the problems of the shape the value was nearest, not of every shape', () => {
    const problems = new SchemaSet({ 'shapes.schema.json': shapes as never }).problems('shapes.schema.json', { load: { strategy: 'range', column: 'd', days: 'many' } })

    expect(problems).toEqual([{ path: ['load', 'days'], message: 'Must be integer.' }])
  })

  it('says nothing when a shape fits', () => {
    expect(new SchemaSet({ 'shapes.schema.json': shapes as never }).problems('shapes.schema.json', { load: { strategy: 'full' } })).toEqual([])
  })
})
