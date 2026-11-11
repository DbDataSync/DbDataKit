import type { Loc, SchemaNode, SchemaSet } from './schemaSet'

/** What kinds of value a schema node allows, from its `type` or what its keywords imply. */
export function typesOf(node: SchemaNode): string[] {
  if (Array.isArray(node.type)) return node.type
  if (typeof node.type === 'string') return [node.type]
  if (node.properties || node.additionalProperties || node.patternProperties) return ['object']
  if (node.items) return ['array']
  return []
}

/** A value a new part starts with: the required parts of an object, an empty list, the constant. Undefined when the schema gives no hint (the part is left for the person to fill). */
export function defaultFor(schemas: SchemaSet, loc: Loc): unknown {
  const { node, loc: real } = schemas.at(loc)
  if (node.default !== undefined) return node.default
  if (node.const !== undefined) return node.const
  const types = typesOf(node)
  if (types.includes('object')) {
    const made: Record<string, unknown> = {}
    for (const key of node.required ?? []) {
      const value = node.properties?.[key] ? defaultFor(schemas, schemas.child(real, 'properties', key)) : undefined
      made[key] = value ?? ''
    }
    return made
  }
  if (types.includes('array')) return []
  if (types.includes('boolean') && !types.includes('string')) return false
  if (types.includes('string')) return ''
  return undefined
}

