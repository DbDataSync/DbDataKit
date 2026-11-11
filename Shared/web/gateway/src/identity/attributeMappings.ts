import type { AttributeMapping } from './api'

/** The longest attribute value the identity home keeps (`DirectoryAttributes.MaxValueLength`). */
export const MAX_ATTRIBUTE_VALUE = 200

/** An attribute name: lower case letters, digits and underscores, starting with a letter, at most 64 (`DirectoryAttributes.IsName`). */
export const ATTRIBUTE_NAME = /^[a-z][a-z0-9_]{0,63}$/

export interface MappingForm { source: string; group: string; attribute: string; value: string }

export interface MappingCheck {
  /** One message per field that is wrong, in the words of the rule (the server checks again and says the same). */
  problems: Partial<Record<'group' | 'attribute' | 'value', string>>
  ok: boolean
  /** Why the Map button is off, or null when it is on. */
  why: string | null
}

/** What can be told before sending: a blank field is not an error yet (nothing is typed), but the button says what is missing. */
export function checkMapping(form: MappingForm, existing: AttributeMapping[]): MappingCheck {
  const problems: MappingCheck['problems'] = {}
  const attribute = form.attribute.trim()
  const value = form.value.trim()
  if (attribute !== '' && !ATTRIBUTE_NAME.test(attribute)) problems.attribute = 'An attribute name is lower case letters, digits and underscores, starting with a letter, at most 64 characters.'
  if (value.length > MAX_ATTRIBUTE_VALUE) problems.value = `A value is at most ${MAX_ATTRIBUTE_VALUE} characters.`
  if (form.group.trim() === '') return { problems, ok: false, why: 'Name the external group.' }
  if (attribute === '') return { problems, ok: false, why: 'Name the attribute.' }
  if (value === '') return { problems, ok: false, why: 'Give the value the group gives.' }
  if (Object.keys(problems).length > 0) return { problems, ok: false, why: Object.values(problems)[0]! }
  if (existing.some((m) => m.source === form.source && m.group === form.group.trim() && m.attribute === attribute && m.value === value)) return { problems, ok: false, why: 'This group already gives that value.' }
  return { problems, ok: true, why: null }
}

/** The request body's fields, trimmed as the server trims them. */
export const mappingRequest = (form: MappingForm) => ({ source: form.source, group: form.group.trim(), attribute: form.attribute.trim(), value: form.value.trim() })

export interface AttributeSummary { attribute: string; values: string[] }

/** What each attribute can be, over all mappings: several mappings of one attribute give several values (the person has them all). */
export function summarise(rows: AttributeMapping[]): AttributeSummary[] {
  const by = new Map<string, Set<string>>()
  for (const m of rows) (by.get(m.attribute) ?? by.set(m.attribute, new Set()).get(m.attribute)!).add(m.value)
  return [...by.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([attribute, values]) => ({ attribute, values: [...values].sort((a, b) => a.localeCompare(b)) }))
}

/** One mapping in a sentence. */
export const sentence = (m: AttributeMapping) => `Members of the ${m.source} group ${m.group} have ${m.attribute} = ${m.value}`

/** Rows in a steady order: by attribute, value, then group. */
export const ordered = (rows: AttributeMapping[]) => [...rows].sort((a, b) => a.attribute.localeCompare(b.attribute) || a.value.localeCompare(b.value) || a.group.localeCompare(b.group))
