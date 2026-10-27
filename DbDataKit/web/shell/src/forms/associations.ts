/** Which schema describes which files of a project, as a server lists them: the first pattern that matches a path wins. */
export interface SchemaAssociation {
  pattern: string
  schema: string
  kind: string
}

/** `**` crosses folders (and may be none), `*` stays within one; everything else is literal. */
export function globToRegExp(pattern: string): RegExp {
  let out = ''
  for (let i = 0; i < pattern.length; i++) {
    if (pattern.startsWith('**/', i)) { out += '(?:.*/)?'; i += 2 }
    else if (pattern.startsWith('**', i)) { out += '.*'; i += 1 }
    else if (pattern[i] === '*') out += '[^/]*'
    else out += pattern[i].replace(/[.+?^${}()|[\]\\]/g, '\\$&')
  }
  return new RegExp(`^${out}$`)
}

export function schemaFor(associations: readonly SchemaAssociation[], path: string): SchemaAssociation | undefined {
  return associations.find((a) => globToRegExp(a.pattern).test(path))
}
