import { describe, expect, it } from 'vitest'
import { MODEL_SCHEMA, isModelName, modelSchema, newModelText, summaryOf } from './storeModel'

describe('names', () => {
  it('accepts what the Store accepts and nothing else', () => {
    for (const ok of ['a', 'sales_territories', 'a-b', '0abc', 'a'.repeat(64)]) expect(isModelName(ok)).toBe(true)
    for (const bad of ['', 'A', '_a', '-a', 'a b', 'a.b', 'a'.repeat(65), '../x']) expect(isModelName(bad)).toBe(false)
  })
  it('starts a new model from a text that names it', () => {
    expect(newModelText('crm')).toContain('name: crm')
    expect(newModelText('')).toContain('name: my_model')
    expect(newModelText('crm')).toContain('schemaVersion: 1')
  })
})

describe('summary', () => {
  it('counts entities and attributes', () => {
    expect(summaryOf(undefined)).toBe('No entities yet.')
    expect(summaryOf({ a: { attributes: { x: 1, y: 2 } }, b: {} })).toBe('2 entities, 2 attributes')
    expect(summaryOf({ a: { attributes: { x: 1 } } })).toBe('1 entity, 1 attribute')
  })
})

describe('schema', () => {
  it('is the form of a model', () => {
    expect(MODEL_SCHEMA).toBeTruthy()
    expect(modelSchema).toBeTruthy()
  })
})
