import type { AttributeSchema, EntitySchema, ModelSchema } from './types'

export function attribute(over: Partial<AttributeSchema> & { name: string }): AttributeSchema {
  return { title: null, type: 'VARCHAR(10)', kind: 'string', length: 10, precision: null, scale: null, required: false, unique: false, default: null, pattern: null, min: null, max: null, allowed: null, references: null, ...over }
}

export const territory: EntitySchema = {
  name: 'territory', title: null, key: ['code'], history: 'none', role: 'table', rules: [],
  attributes: [
    attribute({ name: 'code', required: true }),
    attribute({ name: 'name', length: 100, required: true }),
    attribute({ name: 'tier', allowed: ['gold', 'silver'] }),
    attribute({ name: 'active', kind: 'boolean', type: 'BOOLEAN', length: null }),
    attribute({ name: 'quota', kind: 'decimal', type: 'DECIMAL(10,2)', length: null, precision: 10, scale: 2, min: 0 }),
  ],
}

export const schema: ModelSchema = { name: 'territories', title: null, approval: 'none', entities: [territory] }
