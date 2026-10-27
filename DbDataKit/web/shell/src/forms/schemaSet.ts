import Ajv2020 from 'ajv/dist/2020'
import type { ErrorObject, ValidateFunction } from 'ajv'

/** A JSON Schema node as written in a file; only the keywords the forms read are named. */
export interface SchemaNode {
  $ref?: string
  type?: string | string[]
  title?: string
  description?: string
  properties?: Record<string, SchemaNode>
  required?: string[]
  additionalProperties?: boolean | SchemaNode
  patternProperties?: Record<string, SchemaNode>
  items?: SchemaNode
  enum?: unknown[]
  const?: unknown
  oneOf?: SchemaNode[]
  anyOf?: SchemaNode[]
  if?: SchemaNode
  then?: SchemaNode
  else?: SchemaNode
  default?: unknown
  minItems?: number
  [key: string]: unknown
}

/** A place in a set of schemas: the file it is in (by `$id`) and the JSON pointer inside it. */
export interface Loc {
  base: string
  pointer: string
}

export interface FieldError {
  /** Where in the document, as the path of keys and positions. */
  path: (string | number)[]
  message: string
}

const AjvCtor = ((Ajv2020 as unknown as { default?: typeof Ajv2020 }).default ?? Ajv2020) as typeof Ajv2020

const decode = (segment: string) => segment.replace(/~1/g, '/').replace(/~0/g, '~')

/**
 * A set of schemas that refer to each other (`model.schema.json` hands a file to `declared-model.schema.json`; the folder schema takes its settings from the project schema). Gives the forms the three things they need:
 * the schema node at a place with its references followed, whether a value fits a place (to pick one of several shapes), and the problems a whole document has, each with its path.
 */
export class SchemaSet {
  private readonly ajv = new AjvCtor({ strict: false, allErrors: true, validateFormats: false })
  private readonly files = new Map<string, SchemaNode>()
  private readonly checks = new Map<string, ValidateFunction>()
  private readonly explainers = new Map<string, ValidateFunction>()
  private readonly explainer = new AjvCtor({ strict: false, allErrors: true, validateFormats: false })

  constructor(schemas: Record<string, SchemaNode>) {
    for (const [name, schema] of Object.entries(schemas)) {
      this.files.set(name, schema)
      this.ajv.addSchema(schema as object, name)
    }
  }

  has(name: string) { return this.files.has(name) }

  /** The node at a place, with `$ref` followed (the referrer's description is kept when the target has none) and the place it finally is. */
  at(loc: Loc): { node: SchemaNode; loc: Loc } {
    let current = loc
    let description: string | undefined
    for (let hops = 0; hops < 32; hops++) {
      const node = this.raw(current)
      if (node.description && !description) description = node.description
      if (typeof node.$ref !== 'string') return { node: description && !node.description ? { ...node, description } : node, loc: current }
      const [file, fragment = ''] = node.$ref.split('#')
      current = { base: file === '' ? current.base : file, pointer: fragment }
    }
    throw new Error(`Schema references loop at ${loc.base}#${loc.pointer}`)
  }

  private raw(loc: Loc): SchemaNode {
    let node: unknown = this.files.get(loc.base)
    if (node === undefined) throw new Error(`There is no schema '${loc.base}'.`)
    for (const segment of loc.pointer.split('/').filter((s) => s !== '')) node = (node as Record<string, unknown>)?.[decode(segment)]
    if (node === undefined || node === null || typeof node !== 'object') throw new Error(`Nothing at ${loc.base}#${loc.pointer}.`)
    return node as SchemaNode
  }

  /** The place of a named child of a node (`properties/name`, `oneOf/1`), for the forms to descend with. */
  child(loc: Loc, ...segments: (string | number)[]): Loc {
    return { base: loc.base, pointer: `${loc.pointer}/${segments.map((s) => String(s).replace(/~/g, '~0').replace(/\//g, '~1')).join('/')}` }
  }

  /** Whether the value is valid for the schema at the place. */
  fits(loc: Loc, value: unknown): boolean {
    const key = `${loc.base}#${loc.pointer}`
    let check = this.checks.get(key)
    if (!check) {
      check = this.ajv.compile({ $ref: key } as object)
      this.checks.set(key, check)
    }
    return check(value) as boolean
  }

  /**
   * The schema of a file with every reference replaced by what it points to. Ajv reports the path of a failed keyword from the last reference it followed, which loses where a choice between shapes was made; with nothing to follow,
   * the path is whole and the errors of the shapes that were not meant can be told from those of the one that was.
   */
  private inlined(node: unknown, base: string, followed: string[]): unknown {
    if (Array.isArray(node)) return node.map((n) => this.inlined(n, base, followed))
    if (node === null || typeof node !== 'object') return node
    const object = node as SchemaNode
    if (typeof object.$ref === 'string') {
      const [file, fragment = ''] = object.$ref.split('#')
      const target = { base: file === '' ? base : file, pointer: fragment }
      const key = `${target.base}#${target.pointer}`
      if (followed.includes(key)) return {}                                 // a schema that contains itself is checked one level deep
      const { $ref: _ref, ...siblings } = object
      return { ...(this.inlined(this.raw(target), target.base, [...followed, key]) as object), ...(this.inlined(siblings, base, followed) as object) }
    }
    const copy: Record<string, unknown> = {}
    for (const [k, v] of Object.entries(object)) if (k !== '$defs' && k !== '$id' && k !== '$schema') copy[k] = this.inlined(v, base, followed)
    return copy
  }

  /** What is wrong with a whole document against a file's schema. */
  problems(base: string, value: unknown): FieldError[] {
    let check = this.explainers.get(base)
    if (!check) {
      const root = this.files.get(base)
      if (!root) return []
      check = this.explainer.compile(this.inlined({ ...root, $id: undefined }, base, [`${base}#`]) as object)
      this.explainers.set(base, check)
    }
    check(value)
    return bestExplanations(check.errors ?? []).map(describe)
  }
}

/**
 * Ajv reports, for a value that fits none of several shapes, what is wrong with every shape. A person wants the one they were nearest: for each choice, the errors of the shape with the fewest are kept and the rest (with the
 * useless "must match exactly one schema") dropped. An `if` that failed is dropped too; the branch that applied says what is wrong.
 */
export function bestExplanations(errors: ErrorObject[]): ErrorObject[] {
  const dropped = new Set<ErrorObject>()
  const choices = errors.filter((e) => e.keyword === 'oneOf' || e.keyword === 'anyOf')
  for (const choice of choices) {
    dropped.add(choice)
    const prefix = `${choice.schemaPath}/`
    const branches = new Map<number, ErrorObject[]>()
    for (const e of errors) {
      if (e === choice || !e.schemaPath.startsWith(prefix) || !(e.instancePath === choice.instancePath || e.instancePath.startsWith(`${choice.instancePath}/`))) continue
      const n = Number(e.schemaPath.slice(prefix.length).split('/')[0])
      branches.set(n, [...(branches.get(n) ?? []), e])
    }
    if (branches.size === 0) continue
    const best = [...branches.entries()].sort((a, b) => a[1].length - b[1].length || a[0] - b[0])[0][0]
    for (const [n, list] of branches) if (n !== best) for (const e of list) dropped.add(e)
  }
  return errors.filter((e) => !dropped.has(e) && e.keyword !== 'if')
}

function describe(error: ErrorObject): FieldError {
  const path = error.instancePath.split('/').filter((s) => s !== '').map((s) => (/^\d+$/.test(s) ? Number(s) : decode(s)))
  const where = String(error.params && 'missingProperty' in error.params ? error.params.missingProperty : '')
  switch (error.keyword) {
    case 'required': return { path, message: `\`${where}\` is required.` }
    case 'additionalProperties': return { path, message: `\`${(error.params as { additionalProperty: string }).additionalProperty}\` is not a setting here.` }
    case 'enum': return { path, message: `Must be one of: ${((error.params as { allowedValues: unknown[] }).allowedValues).join(', ')}.` }
    case 'type': return { path, message: `Must be ${String((error.params as { type: string }).type).replace(',', ' or ')}.` }
    default: return { path, message: error.message ?? 'Not valid.' }
  }
}
