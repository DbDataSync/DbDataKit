import { describe, expect, it } from 'vitest'
import { schemaFor, type SchemaAssociation } from './associations'

const ASSOCIATIONS: SchemaAssociation[] = [
  { pattern: 'models/**/_dbdatabuild.yml', schema: 'folder.schema.json', kind: 'folder settings' },
  { pattern: 'models/**/*.yml', schema: 'model.schema.json', kind: 'model' },
  { pattern: 'tests/models/**/*.yml', schema: 'model-test.schema.json', kind: 'model test' },
  { pattern: 'dbdatabuild.yml', schema: 'config.schema.json', kind: 'project' },
]

describe('which schema a file has', () => {
  const of = (path: string) => schemaFor(ASSOCIATIONS, path)?.schema

  it('matches folders at any depth, including none', () => {
    expect(of('models/orders.yml')).toBe('model.schema.json')
    expect(of('models/marts/fct_orders.yml')).toBe('model.schema.json')
    expect(of('models/a/b/c/d.yml')).toBe('model.schema.json')
  })

  it('takes the first pattern that matches, so a folder file is not a model', () => {
    expect(of('models/marts/_dbdatabuild.yml')).toBe('folder.schema.json')
    expect(of('models/_dbdatabuild.yml')).toBe('folder.schema.json')
  })

  it('does not let a star cross a folder or match another extension', () => {
    expect(of('dbdatabuild.yml')).toBe('config.schema.json')
    expect(of('sub/dbdatabuild.yml')).toBeUndefined()
    expect(of('models/orders.sql')).toBeUndefined()
    expect(of('tests/models/marts/fct_orders.yml')).toBe('model-test.schema.json')
    expect(of('tests/metadata/rule.sql')).toBeUndefined()
  })

  it('treats the dots of a pattern as dots', () => {
    expect(of('models/ordersXyml')).toBeUndefined()
  })
})
