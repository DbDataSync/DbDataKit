import type { WidgetDoc, ViewsDoc } from '../views/model'
import type { CatalogRoles } from '../views/derive'
import { roleOf } from '../views/derive'
import { ICON_NAMES } from '../views/icons'
import { checkGroups } from '../views/groups'
import type { GroupDef } from '../views/groups'
import { Issues } from '../views/common'
import { wellsOf } from './wells'
import type { WellSpec } from './wells'
import type { Path } from './yamlEdit'

/**
 * The entries of the wells as things with options of their own (plan 32 section 9h, DBR54): a measure that may be repeated under an **alias** and narrowed by its own **filters** (DBR50), an **attribute** in a matrix's
 * cell area or a table column with its **display**, **icons**, **many** policy and **link** (DBR51), and a **group** named in a channel or an axis (DBR52). Pure functions of the widget: nothing here draws, each change is a
 * `setPath` of the entry (so the text keeps its comments and order), and the words of a problem are the loaders'.
 */

export interface EntryRef {
  well: string
  index: number
  /** Where the entry is in the widget: `['encodings', 'y', 1]`, or `['encodings', 'value']` for a well that holds one entry as itself. */
  path: Array<string | number>
  /** What the entry is: a `measure` (a field the catalog says is a measure, or any field of a measure well), an `attribute` (an attribute entry of the cell area, or a field of an attribute role) or a `group`. */
  kind: 'measure' | 'attribute' | 'group'
  /** The field, the attribute or `@group`. */
  field: string
  /** The entry as written: a name, or its object form. */
  entry: unknown
  /** The well takes measures (so alias and filters apply), and attribute entries (display and the rest apply). */
  takesMeasure: boolean
  attributeEntry: boolean
  /** A table column (it has no `many`). */
  table: boolean
}

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const clone = <T>(v: T): T => structuredClone(v)
const fieldName = (e: unknown): string | undefined =>
  typeof e === 'string' && e.length > 0 ? e : isObject(e) ? (typeof e.field === 'string' ? e.field : typeof e.attribute === 'string' ? e.attribute : typeof e.group === 'string' ? `@${e.group}` : undefined) : undefined

function at(node: unknown, path: readonly string[]): unknown {
  let n = node
  for (const k of path) n = isObject(n) ? n[k] : undefined
  return n
}

/** The entries of a widget's wells that have options (a measure, an attribute entry, a group), in well order. `roles` tell a chart channel or a table column that is a measure from one that is an attribute. */
export function entryRefs(widget: WidgetDoc, roles?: CatalogRoles | null): EntryRef[] {
  const refs: EntryRef[] = []
  for (const spec of wellsOf(widget.type)) {
    const node = at(widget, spec.path)
    const list = node === undefined ? [] : Array.isArray(node) ? node : [node]
    const asList = Array.isArray(node)
    list.forEach((entry, index) => {
      const field = fieldName(entry)
      if (field === undefined) return
      const isGroup = isObject(entry) && typeof entry.group === 'string'
      const isAttribute = isObject(entry) && typeof entry.attribute === 'string'
      const role = isGroup || isAttribute ? 'attribute' : roles ? roleOf(roles, field) : spec.kind === 'measure' ? 'measure' : 'unknown'
      const takesMeasure = !isGroup && !isAttribute && (spec.kind === 'measure' || (spec.kind === 'either' && role !== 'attribute' && (role === 'measure' || spec.prefer === 'measure')))
      refs.push({
        well: spec.id, index, path: asList ? [...spec.path, index] : [...spec.path], kind: isGroup ? 'group' : takesMeasure ? 'measure' : 'attribute', field, entry,
        takesMeasure, attributeEntry: isAttribute || (!isGroup && !takesMeasure && widget.type === 'table'), table: widget.type === 'table',
      })
    })
  }
  return refs
}

export const ALIAS_PATTERN = /^[a-z][a-z0-9_]{0,63}$/

/** What is wrong with an entry's options, in the loaders' words (`DBD-106`), keyed by the option. */
export function entryProblems(entry: unknown, matrix: boolean): Record<string, string> {
  const problems: Record<string, string> = {}
  if (!isObject(entry)) return problems
  if (entry.alias !== undefined && !(typeof entry.alias === 'string' && ALIAS_PATTERN.test(entry.alias)))
    problems.alias = '`alias` is a new snake_case name for the output: lower-case letters, digits and \'_\', starting with a letter (at most 64).'
  if (Array.isArray(entry.filters) && entry.filters.length > 50) problems.filters = '`filters` has at most 50 entries.'
  const icons = isObject(entry.icons) ? entry.icons : undefined
  if (icons) {
    for (const [value, icon] of Object.entries(icons)) if (!(ICON_NAMES as readonly string[]).includes(String(icon))) problems.icons = `An icon is one of: ${ICON_NAMES.join(', ')} (the icon of \`${value}\` is not).`
    if (entry.display !== undefined && entry.display !== 'icon') problems.icons = '`icons` belong to `display: icon`.'
  } else if (entry.display === 'icon') problems.display = '`display: icon` needs `icons`: the icon each value shows.'
  if (entry.many !== undefined && !matrix) problems.many = '`many` is for an attribute entry of a matrix: a table row has one value.'
  return problems
}

/** The path in the document and the value that set `key` of an entry (undefined removes it); a name becomes its object form, and an object left with nothing but its field is a name again where the well takes one. */
export function entryChange(id: string, widget: WidgetDoc, ref: EntryRef, key: string, value: unknown): { path: Path; value: unknown } {
  const base: Record<string, unknown> = isObject(ref.entry) ? clone(ref.entry) : { [ref.kind === 'attribute' && ref.attributeEntry && widget.type === 'matrix' ? 'attribute' : 'field']: ref.field }
  if (value === undefined || value === '' || (Array.isArray(value) && value.length === 0) || (isObject(value) && Object.keys(value).length === 0)) delete base[key]
  else base[key] = value
  const keys = Object.keys(base)
  const plainAllowed = widget.type === 'chart' || widget.type === 'metric'
  const result = plainAllowed && keys.length === 1 && keys[0] === 'field' ? (base.field as string) : base
  return { path: ['widgets', id, ...ref.path], value: result }
}

/** A measure's own filter added or removed (`filters` of the entry; an empty list removes the key, so the alias is the only thing left). */
export type EntryFilterChange = { op: 'add'; filter: Record<string, unknown> } | { op: 'remove'; index: number }
export function entryFilterChange(id: string, widget: WidgetDoc, ref: EntryRef, change: EntryFilterChange): { path: Path; value: unknown } {
  const current = isObject(ref.entry) && Array.isArray(ref.entry.filters) ? [...ref.entry.filters] : []
  const next = change.op === 'add' ? [...current, change.filter] : current.filter((_, i) => i !== change.index)
  return entryChange(id, widget, ref, 'filters', next)
}

/** The icon of one value of an attribute entry set or cleared (`undefined`); the map is removed when nothing is left in it. */
export function entryIconChange(id: string, widget: WidgetDoc, ref: EntryRef, value: string, icon: string | undefined): { path: Path; value: unknown } {
  const icons = isObject(ref.entry) && isObject(ref.entry.icons) ? { ...ref.entry.icons } : {}
  if (icon === undefined) delete icons[value]
  else icons[value] = icon
  return entryChange(id, widget, ref, 'icons', icons)
}

/** The document's groups as the editor and the derivation read them (a document with a problem in a group shows the ones that read). */
export function groupDefsOf(doc: { groups?: unknown } | null | undefined): Record<string, GroupDef> {
  return doc?.groups === undefined ? {} : checkGroups(doc.groups, new Issues())
}

/** What is wrong with one group as it is being typed, in the loaders' words. */
export function groupProblems(name: string, def: unknown): string[] {
  const issues = new Issues()
  checkGroups({ [name]: def }, issues)
  return issues.list.map((i) => i.message)
}

/** The widgets of a document that name a group (in a channel or an axis): a group in use is not removed. */
export function groupUses(doc: ViewsDoc, name: string): string[] {
  const uses: string[] = []
  const walk = (node: unknown): boolean => Array.isArray(node) ? node.some(walk) : isObject(node) ? Object.entries(node).some(([k, v]) => (k === 'group' && v === name) || (k !== 'query' && walk(v))) : false
  for (const [id, w] of Object.entries(doc.widgets ?? {})) if (walk((w as { encodings?: unknown }).encodings)) uses.push(id)
  return uses
}

/** The names of the links of a document: the choices of an attribute entry's `link`. */
export const linkNamesOf = (doc: ViewsDoc): string[] => Object.keys((doc as { links?: Record<string, unknown> }).links ?? {})

export type { WellSpec }
