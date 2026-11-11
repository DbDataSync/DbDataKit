import type { Catalog, CatalogMember } from '../views/catalog'
import { memberProblem } from '../views/derive'
import type { CatalogRoles } from '../views/derive'
import type { ParameterDefinition, WidgetDoc } from '../views/model'
import { EditError } from './errors'
import type { Op, Path } from './yamlEdit'

/**
 * The Filter and Sort sections of the widget editor (plan 32 DBR43), as pure functions of a widget: the operators the catalog allows for a field's type, the filter and order entries a form writes into the
 * derived query (`query.filters`, `query.order`), the sort a table, a matrix axis or a chart channel carries, and what is wrong with each entry in the engine's own words (`Validator.CheckLeaf`), so the control
 * says what the server will say. Nothing here draws. A filter or an order entry a form cannot say stays valid YAML and shows as it is.
 */

/** What the engine calls the type of a filter's attribute: `string` (and a string of periods, `time`), a number of any width, a `date` or a `datetime`. */
export type FilterType = 'string' | 'number' | 'date' | 'datetime'

const STRING_OPERATORS = ['equals', 'not_equals', 'contains', 'not_contains', 'starts_with', 'set', 'not_set'] as const
const TIME_STRING_OPERATORS = [...STRING_OPERATORS, 'gt', 'gte', 'lt', 'lte', 'between'] as const
const ORDERED_OPERATORS = ['equals', 'not_equals', 'gt', 'gte', 'lt', 'lte', 'between', 'set', 'not_set'] as const
/** Relative to today: they take a period attribute, and no values (`last_complete` takes a count). */
export const RELATIVE_OPERATORS = ['last_complete', 'current', 'period_to_date'] as const
/** The operators that take no value. */
const NO_VALUES = new Set<string>(['set', 'not_set', ...RELATIVE_OPERATORS])

/** The type of a catalog member as the engine's validator reads it: a member the catalog does not describe reads as a string. */
export function filterTypeOf(member?: CatalogMember | null): FilterType {
  const t = (member?.type ?? 'string').toLowerCase()
  if (t === 'date') return 'date'
  if (t === 'datetime' || t === 'timestamp') return 'datetime'
  if (['integer', 'int', 'number', 'decimal', 'float', 'double', 'long', 'numeric'].includes(t)) return 'number'
  return 'string'
}

/** The operators the catalog allows on a member, as the engine's validator does: ordered types take the comparisons, a string takes the text operators (and the comparisons when it holds periods), a period attribute also takes the relative ones. */
export function operatorsFor(member?: CatalogMember | null): string[] {
  const type = filterTypeOf(member)
  const base: readonly string[] = type === 'string' ? (member?.time ? TIME_STRING_OPERATORS : STRING_OPERATORS) : ORDERED_OPERATORS
  return member?.time || type === 'date' || type === 'datetime' ? [...base, ...RELATIVE_OPERATORS] : [...base]
}

/** A filter value: written out, or a document parameter (`{param: name}`). */
export type FilterValue = string | number | boolean | { param: string }
export interface FilterEntry { attribute: string; operator: string; values?: FilterValue[] | { param: string }; count?: number }

/** What is wrong with one filter, by the engine's code, at a key of the entry (`attribute`, `operator`, `values`, `count`), in its words. */
export interface QueryProblem { code: string; key: string; message: string }

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const dirPart = (v: unknown): { dir?: Direction } => (v === 'desc' || v === 'asc' ? { dir: v } : {})
const valueCount = (v: FilterEntry['values']): number => (Array.isArray(v) ? v.length : v === undefined ? 0 : 1)

/**
 * The problems of a filter entry, in the engine's words: an attribute the catalog lacks (`unknown_member`), a measure where an attribute goes (`wrong_member_kind`), an operator that does not fit the attribute's type
 * (`invalid_operator`: "gt can't be used on string member customers.region."), a missing value (`missing_values`), a count that is not `last_complete`'s (`period_count_invalid`). Without a catalog only the shape is checked.
 */
export function filterProblems(entry: FilterEntry, catalog?: Catalog | null): QueryProblem[] {
  const found: QueryProblem[] = []
  const roles = catalog ? rolesOf(catalog) : null
  const member = catalog?.attributes.find((m) => m.name === entry.attribute)
  if (roles && entry.attribute !== '') {
    const problem = memberProblem(roles, entry.attribute, 'attribute')
    if (problem) { found.push({ code: problem.code, key: 'attribute', message: problem.message }); return found }
  }
  if (entry.count !== undefined && entry.operator !== 'last_complete')
    found.push({ code: 'period_count_invalid', key: 'count', message: `count belongs to last_complete (how many whole periods); ${entry.operator} takes none.` })
  if (entry.operator === 'last_complete' && !(Number.isInteger(entry.count) && (entry.count ?? 0) >= 1))
    found.push({ code: 'period_count_invalid', key: 'count', message: 'last_complete needs a count of at least 1 (how many whole periods).' })
  if (RELATIVE_OPERATORS.includes(entry.operator as never)) {
    if (valueCount(entry.values) > 0) found.push({ code: 'period_operator_takes_values', key: 'values', message: `${entry.operator} is relative to today and takes no values.` })
    return found
  }
  if (member && !operatorsFor(member).includes(entry.operator))
    found.push({ code: 'invalid_operator', key: 'operator', message: `${entry.operator} can't be used on ${member.type ?? 'string'} member ${entry.attribute}.` })
  if (!NO_VALUES.has(entry.operator) && valueCount(entry.values) === 0) found.push({ code: 'missing_values', key: 'values', message: `${entry.operator} needs at least one value.` })
  return found
}

function rolesOf(catalog: Catalog): CatalogRoles {
  return { model: catalog.model, measures: catalog.measures.map((m) => m.name), attributes: catalog.attributes.map((m) => m.name) }
}

/** The value a form writes for a parameter: a many-valued parameter supplies the whole list (`values: {param: name}`), any other one value (`values: [{param: name}]`). */
export function parameterValue(name: string, parameters: readonly ParameterDefinition[] = []): NonNullable<FilterEntry['values']> {
  return parameters.find((p) => p.name === name)?.many === true ? { param: name } : [{ param: name }]
}

/** A filter entry for the form's fields: an operator that takes no value carries none; `between` and the rest carry the values as typed. */
export function filterEntry(attribute: string, operator: string, values: FilterEntry['values'], count?: number): FilterEntry {
  const entry: FilterEntry = { attribute, operator }
  if (!NO_VALUES.has(operator) && values !== undefined && valueCount(values) > 0) entry.values = values
  if (operator === 'last_complete' && count !== undefined) entry.count = count
  return entry
}

/** The value a typed text means for an attribute: a number for a number type, text otherwise; `a, b` is two values. */
export function parseValues(text: string, member?: CatalogMember | null): FilterValue[] {
  const type = filterTypeOf(member)
  return text.split(',').map((s) => s.trim()).filter((s) => s.length > 0).map((s) => (type === 'number' && /^-?\d+(\.\d+)?$/.test(s) ? Number(s) : s))
}

// ---- changes ----

export type FilterChange =
  | { op: 'add'; filter: FilterEntry; index?: number }
  | { op: 'set'; index: number; filter: FilterEntry }
  | { op: 'remove'; index: number }
  | { op: 'move'; from: number; to: number }

const QUERY_TYPES = ['metric', 'chart', 'matrix', 'table']

function derivedQueryOf(widget: WidgetDoc, id: string): Record<string, unknown> {
  if (!QUERY_TYPES.includes(widget.type)) throw new EditError('bad_value', `A ${widget.type} widget has no query, so there is nothing to filter or sort.`)
  const query = (widget as { query?: unknown }).query
  const q = isObject(query) ? query : {}
  if (q.mode === 'explicit') throw new EditError('bad_value', `The query of \`${id}\` is written out (\`mode: explicit\`): its filters and order are in the request, not in the form.`)
  return q
}

function listAt(q: Record<string, unknown>, key: 'filters' | 'order'): unknown[] {
  return Array.isArray(q[key]) ? (q[key] as unknown[]) : []
}

function position(list: unknown[], index: number, what: string): void {
  if (!Number.isInteger(index) || index < 0 || index >= list.length) throw new EditError('bad_position', `There is no ${what} ${index + 1}.`)
}

/** The first problem that refuses a filter outright (an operator the field's type does not take, a measure as the attribute): what a command will not write, in the engine's words. An unknown member is written and flagged, as a well does. */
function refusal(entry: FilterEntry, catalog?: Catalog | null): string | undefined {
  if (entry.attribute.trim() === '') return 'Name an attribute to filter on.'
  if (entry.operator.trim() === '') return 'Choose an operator.'
  const problem = filterProblems(entry, catalog).find((p) => ['wrong_member_kind', 'invalid_operator'].includes(p.code))
  return problem?.message
}

/** The changes of the text a filter change makes. A list that is left empty goes with its key. */
export function filterOps(widget: WidgetDoc, id: string, change: FilterChange, catalog?: Catalog | null): Op[] {
  const q = derivedQueryOf(widget, id)
  const list = listAt(q, 'filters')
  const path: Path = ['widgets', id, 'query', 'filters']
  switch (change.op) {
    case 'add': {
      const why = refusal(change.filter, catalog)
      if (why) throw new EditError('bad_value', why)
      if (list.length === 0) return [{ op: 'set', path, value: [change.filter] }]
      return [{ op: 'insert', path, index: Math.max(0, Math.min(list.length, change.index ?? list.length)), value: change.filter }]
    }
    case 'set': {
      position(list, change.index, 'filter')
      const why = refusal(change.filter, catalog)
      if (why) throw new EditError('bad_value', why)
      return [{ op: 'set', path: [...path, change.index], value: change.filter }]
    }
    case 'remove':
      position(list, change.index, 'filter')
      return list.length === 1 ? [{ op: 'remove', path }] : [{ op: 'remove', path: [...path, change.index] }]
    case 'move':
      position(list, change.from, 'filter')
      position(list, change.to, 'filter')
      return change.from === change.to ? [] : [{ op: 'move', path, from: change.from, to: change.to }]
  }
}

// ---- order ----

export type Direction = 'asc' | 'desc'
export type OrderChange =
  | { op: 'add'; field: string; dir?: Direction }
  | { op: 'direction'; index: number; dir: Direction }
  | { op: 'remove'; index: number }
  | { op: 'move'; from: number; to: number }

/** An order entry as the document holds it: `[field, dir]` (or the engine's `{by, dir}`), read as a field and a direction. */
export function orderEntry(entry: unknown): { field: string; dir: Direction } | null {
  if (Array.isArray(entry) && typeof entry[0] === 'string') return { field: entry[0], dir: entry[1] === 'desc' ? 'desc' : 'asc' }
  if (isObject(entry) && typeof entry.by === 'string') return { field: entry.by, dir: entry.dir === 'desc' ? 'desc' : 'asc' }
  return null
}

/** What is wrong with the fields of the order entries: one the catalog lacks (`unknown_member`, in the engine's words). */
export function orderProblems(list: readonly unknown[], catalog?: Catalog | null): Array<QueryProblem & { index: number }> {
  if (!catalog) return []
  const roles = rolesOf(catalog)
  const found: Array<QueryProblem & { index: number }> = []
  list.forEach((e, index) => {
    const entry = orderEntry(e)
    const problem = entry ? memberProblem(roles, entry.field, 'either') : null
    if (problem) found.push({ index, code: problem.code, key: 'field', message: problem.message })
  })
  return found
}

/** The changes of the text an order change makes: the query's `order` as a list of `[field, dir]`. */
export function orderOps(widget: WidgetDoc, id: string, change: OrderChange): Op[] {
  const q = derivedQueryOf(widget, id)
  const list = listAt(q, 'order')
  const path: Path = ['widgets', id, 'query', 'order']
  switch (change.op) {
    case 'add': {
      const field = change.field.trim()
      if (field === '') throw new EditError('bad_field', 'Name a field to sort by.')
      const entry = [field, change.dir ?? 'asc']
      return list.length === 0 ? [{ op: 'set', path, value: [entry] }] : [{ op: 'insert', path, index: list.length, value: entry }]
    }
    case 'direction': {
      position(list, change.index, 'order entry')
      const entry = orderEntry(list[change.index])
      if (!entry) throw new EditError('bad_value', `Order entry ${change.index + 1} is written in a form the editor does not change; edit it in the YAML view.`)
      return [{ op: 'set', path: [...path, change.index], value: [entry.field, change.dir] }]
    }
    case 'remove':
      position(list, change.index, 'order entry')
      return list.length === 1 ? [{ op: 'remove', path }] : [{ op: 'remove', path: [...path, change.index] }]
    case 'move':
      position(list, change.from, 'order entry')
      position(list, change.to, 'order entry')
      return change.from === change.to ? [] : [{ op: 'move', path, from: change.from, to: change.to }]
  }
}

// ---- the sort a table, a matrix axis or a chart channel carries ----

/** Where a sort is written: a table's `encodings.sort`, a matrix axis (`encodings.sort.rows`), or a chart channel's own `sort` (`ascending`, `descending`, `sort_key`). */
export type SortTarget = { kind: 'table' } | { kind: 'axis'; axis: 'rows' | 'columns' } | { kind: 'channel'; channel: 'x' | 'y' | 'color' | 'column' | 'row' }
/** `by` is a field, and for an axis also `sort_key` and `label`; undefined removes the sort. A channel takes only the direction (`dir`) or `sort_key`. */
export type SortValue = { by: string; dir?: Direction } | undefined

export const CHANNEL_SORTS = ['ascending', 'descending', 'sort_key'] as const

/** The sort a widget carries at a target, as `{by, dir}` (a channel's keyword reads as `sort_key`, or as the field with a direction); undefined when it carries none. */
export function sortAt(widget: WidgetDoc, target: SortTarget): SortValue {
  const encodings = isObject((widget as { encodings?: unknown }).encodings) ? ((widget as { encodings: Record<string, unknown> }).encodings) : {}
  if (target.kind === 'table') return isObject(encodings.sort) && typeof encodings.sort.by === 'string' ? { by: encodings.sort.by, ...dirPart(encodings.sort.dir) } : undefined
  if (target.kind === 'axis') {
    const sort = isObject(encodings.sort) ? encodings.sort[target.axis] : undefined
    const inEntry = Array.isArray(encodings[target.axis]) ? (encodings[target.axis] as unknown[]).map((e) => (isObject(e) ? e.sort : undefined)).find((s) => s !== undefined) : undefined
    const found = sort ?? inEntry
    return isObject(found) && typeof found.by === 'string' ? { by: found.by, ...dirPart(found.dir) } : undefined
  }
  const channel = encodings[target.channel]
  const sort = isObject(channel) ? channel.sort : undefined
  if (sort === 'ascending') return { by: 'field', dir: 'asc' }
  if (sort === 'descending') return { by: 'field', dir: 'desc' }
  return sort === 'sort_key' ? { by: 'sort_key' } : undefined
}

function fieldOfChannel(node: unknown): string | undefined {
  return typeof node === 'string' ? node : isObject(node) && typeof node.field === 'string' ? node.field : undefined
}

/**
 * The changes of the text a sort change makes. A table's sort is `{by, dir}` (a field the table shows or not); a matrix axis's is `{by, dir}` with `sort_key`, `label` or a measure; a matrix whose axis entry
 * carries the sort (`{field, sort}`) keeps it there; a channel's sort is a keyword, and a shorthand channel (`x: orders.month`) becomes `{field, sort}`. `catalog` refuses a `by` that is not a catalog member,
 * and a measure only where the view reads one (a matrix axis), in the engine's words.
 */
export function sortOps(widget: WidgetDoc, id: string, target: SortTarget, sort: SortValue, catalog?: Catalog | null): Op[] {
  const allowed = target.kind === 'table' ? ['table'] : target.kind === 'axis' ? ['matrix'] : ['chart']
  if (!allowed.includes(widget.type)) throw new EditError('bad_value', `A ${widget.type} widget has no ${target.kind === 'axis' ? 'axis' : target.kind === 'table' ? 'table' : 'channel'} to sort.`)
  const encodings = isObject((widget as { encodings?: unknown }).encodings) ? ((widget as { encodings: Record<string, unknown> }).encodings) : {}
  const base: Path = ['widgets', id, 'encodings']
  if (target.kind === 'channel') {
    const node = encodings[target.channel]
    const field = fieldOfChannel(node)
    if (field === undefined) throw new EditError('bad_value', `The channel \`${target.channel}\` has no field to sort.`)
    if (sort !== undefined && sort.by !== 'field' && sort.by !== 'sort_key') throw new EditError('bad_value', "A channel's sort is ascending, descending or sort_key: a chart cannot sort a channel by another field.")
    const keyword = sort === undefined ? undefined : sort.by === 'sort_key' ? 'sort_key' : sort.dir === 'desc' ? 'descending' : 'ascending'
    if (isObject(node)) return keyword === undefined ? (node.sort === undefined ? [] : [{ op: 'remove', path: [...base, target.channel, 'sort'] }]) : [{ op: 'set', path: [...base, target.channel, 'sort'], value: keyword }]
    return keyword === undefined ? [] : [{ op: 'set', path: [...base, target.channel], value: { field, sort: keyword } }]
  }
  if (sort !== undefined) {
    const by = sort.by.trim()
    if (by === '') throw new EditError('bad_field', 'Name a field to sort by.')
    const keyword = target.kind === 'axis' && (by === 'sort_key' || by === 'label')
    if (!keyword && catalog) {
      const problem = memberProblem(rolesOf(catalog), by, target.kind === 'axis' ? 'measure' : 'either')
      if (problem?.code === 'wrong_member_kind') throw new EditError('wrong_member_kind', problem.message)
    }
  }
  if (target.kind === 'table') {
    if (sort === undefined) return encodings.sort === undefined ? [] : [{ op: 'remove', path: [...base, 'sort'] }]
    return [{ op: 'set', path: [...base, 'sort'], value: { by: sort.by.trim(), ...(sort.dir ? { dir: sort.dir } : {}) } }]
  }
  // a matrix axis: where the sort already is (an entry's own, or `sort.<axis>`), else `sort.<axis>`
  const entries = Array.isArray(encodings[target.axis]) ? (encodings[target.axis] as unknown[]) : []
  const at = entries.findIndex((e) => isObject(e) && e.sort !== undefined)
  const value = sort === undefined ? undefined : { by: sort.by.trim(), ...(sort.dir ? { dir: sort.dir } : {}) }
  if (at >= 0) return value === undefined ? [{ op: 'remove', path: [...base, target.axis, at, 'sort'] }] : [{ op: 'set', path: [...base, target.axis, at, 'sort'], value }]
  const all = isObject(encodings.sort) ? encodings.sort : undefined
  if (value === undefined) {
    if (all?.[target.axis] === undefined) return []
    return Object.keys(all).length === 1 ? [{ op: 'remove', path: [...base, 'sort'] }] : [{ op: 'remove', path: [...base, 'sort', target.axis] }]
  }
  return all === undefined ? [{ op: 'set', path: [...base, 'sort'], value: { [target.axis]: value } }] : [{ op: 'set', path: [...base, 'sort', target.axis], value }]
}
