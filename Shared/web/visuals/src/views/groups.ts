import { Issues, VIEW, defs, isObject, join, part, run, at } from './common'
import { checkFormat } from './format'

/**
 * The field groups of a document (plan 32 section 9h, DBR52), as `ViewsLoader.ReadGroups` reads them in .NET: `groups: {name: {fields, label, template}}`, each group two to four attributes, outermost first, each a field
 * name or `{field, format}`, labelled by `template` (`{1}` is the first level's text; the levels joined by a space when it is left out).
 */

export interface GroupLevel { field: string; format?: unknown }
export interface GroupDef { name: string; levels: GroupLevel[]; label?: string; template?: string }

const groupCheck = part(defs.group!)

export const MAXIMUM_GROUP_LEVELS = 4
export const MINIMUM_GROUP_LEVELS = 2

/** The groups of a `groups` value that read, by name; every fault is an issue. */
export function checkGroups(raw: unknown, issues: Issues, base = 'groups'): Record<string, GroupDef> {
  const found: Record<string, GroupDef> = {}
  if (!isObject(raw)) { issues.add('DBD-106', base, '`groups` must be a mapping.'); return found }
  const entries = Object.entries(raw)
  if (entries.length > 50) { issues.add('DBD-106', base, 'A document has at most 50 groups.'); return found }
  for (const [name, def] of entries) {
    const path = join(base, name)
    if (!/^[a-z][a-z0-9_-]{0,63}$/.test(name)) { issues.add('DBD-106', path, `\`${name}\` is not a group name: lower-case letters, digits, '-' and '_', starting with a letter.`); continue }
    if (!isObject(def)) { issues.add('DBD-106', path, `The group \`${name}\` must be a mapping.`); continue }
    const before = issues.list.length
    run(groupCheck, def, { kind: 'widget', type: 'group' }, path, issues)
    const levels: GroupLevel[] = []
    if (Array.isArray(def.fields))
      def.fields.forEach((f, i) => {
        const lp = at(join(path, 'fields'), i)
        if (typeof f === 'string' && f.length > 0) levels.push({ field: f })
        else if (isObject(f) && typeof f.field === 'string' && f.field.length > 0) {
          if (f.format !== undefined) {
            const problem = checkFormat(f.format)
            if (problem) issues.add('DBD-106', problem.key !== '' && isObject(f.format) ? join(join(lp, 'format'), problem.key) : join(lp, 'format'), problem.message)
          }
          levels.push({ field: f.field, format: f.format })
        }
      })
    if (new Set(levels.map((l) => l.field)).size !== levels.length) issues.add('DBD-106', join(path, 'fields'), 'A field is listed once in a group.')
    if (typeof def.template === 'string')
      for (const m of def.template.matchAll(/\{([0-9]+)\}/g)) {
        const n = Number(m[1])
        if (n < 1 || n > Math.max(levels.length, MINIMUM_GROUP_LEVELS)) issues.add('DBD-106', join(path, 'template'), `\`${m[0]}\` names a level the group does not have: use {1} to {${Math.max(levels.length, MINIMUM_GROUP_LEVELS)}}.`)
      }
    if (issues.list.length === before && levels.length >= MINIMUM_GROUP_LEVELS)
      found[name] = { name, levels, label: typeof def.label === 'string' ? def.label : undefined, template: typeof def.template === 'string' ? def.template : undefined }
  }
  return found
}

/** A reference to a group the document does not declare (`DBD-VIEW-047`). */
export const unknownGroup = (name: string, groups: Record<string, GroupDef>): string =>
  `\`${name}\` is not a group of this document.${Object.keys(groups).length === 0 ? ' Declare it under `groups`.' : ` Groups: ${Object.keys(groups).join(', ')}.`}`
export const UNKNOWN_GROUP = VIEW('047')
export const GROUP_CONFLICT = VIEW('048')
