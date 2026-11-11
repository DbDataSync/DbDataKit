import { isMap, isScalar, isSeq, parseDocument, stringify, type Document } from 'yaml'

/** Where a value is in a document: keys of mappings and positions in lists. */
export type YamlPath = (string | number)[]

export interface ParsedYaml {
  /** The document as plain values; undefined when the text is empty or does not parse. */
  value: unknown
  /** What the YAML parser found wrong, in words; empty when the text parses. */
  errors: string[]
}

export function parseYaml(text: string): ParsedYaml {
  const doc = parseDocument(text)
  const errors = doc.errors.map((e) => `line ${e.linePos?.[0].line ?? '?'}: ${e.message.split('\n')[0]}`)
  return { value: errors.length > 0 ? undefined : (doc.toJS() ?? undefined), errors }
}

function edit(text: string, change: (doc: Document) => void): string {
  const doc = parseDocument(text)
  if (doc.errors.length > 0) throw new Error('The text is not valid YAML, so a form cannot change it. Fix it in the YAML tab first.')
  if (doc.contents == null) doc.contents = doc.createNode({}) as never
  change(doc)
  return doc.toString({ lineWidth: 0 })
}

/**
 * The text with the value at the path set. Only the nodes on the path change: comments, key order, quoting and flow or block style elsewhere (and the style and comments of the node itself when both old and new are plain values) are kept,
 * which is what lets a form and the YAML tab edit the same file.
 */
export function setAt(text: string, path: YamlPath, value: unknown): string {
  const spliced = spliceScalar(text, path, value)
  if (spliced !== null) return spliced
  return edit(text, (doc) => {
    if (path.length === 0) { doc.contents = doc.createNode(value) as never; return }
    // a missing parent is made as a mapping, or a list when the next step is a position
    for (let i = 1; i < path.length; i++) if (!doc.hasIn(path.slice(0, i))) doc.setIn(path.slice(0, i), doc.createNode(typeof path[i] === 'number' ? [] : {}))
    doc.setIn(path, value)
  })
}

const isPlain = (value: unknown): value is string | number | boolean | null => value === null || ['string', 'number', 'boolean'].includes(typeof value)

/** A scalar replaced by a scalar is a change of those characters and nothing else, so the rest of the file is byte for byte what it was. Null when the edit is not that (a new key, a collection, a multi-line value). */
function spliceScalar(text: string, path: YamlPath, value: unknown): string | null {
  if (path.length === 0 || !isPlain(value)) return null
  const doc = parseDocument(text)
  if (doc.errors.length > 0) return null
  const node = doc.getIn(path, true)
  if (!isScalar(node) || !node.range || node.range[0] === node.range[1]) return null     // an empty value has no characters to replace
  const written = stringify(value, { lineWidth: 0 }).trimEnd()
  if (written.includes('\n')) return null
  return text.slice(0, node.range[0]) + written + text.slice(node.range[1])
}

/** The text without the value at the path (a key of a mapping or an item of a list); a mapping left empty stays, as `{}`. */
export function removeAt(text: string, path: YamlPath): string {
  return edit(text, (doc) => { doc.deleteIn(path) })
}

/** The text with an item added at the end of the list at the path (made if it is missing). */
export function appendAt(text: string, path: YamlPath, value: unknown): string {
  return edit(text, (doc) => {
    if (!doc.hasIn(path)) doc.setIn(path, doc.createNode([]))
    const list = doc.getIn(path, true)
    if (!isSeq(list)) throw new Error('That is not a list.')
    list.add(doc.createNode(value))
  })
}

/** The text with the item at `from` moved to `to` in the list at the path. */
export function moveIn(text: string, path: YamlPath, from: number, to: number): string {
  return edit(text, (doc) => {
    const list = doc.getIn(path, true)
    if (!isSeq(list) || from < 0 || to < 0 || from >= list.items.length || to >= list.items.length) return
    const [item] = list.items.splice(from, 1)
    list.items.splice(to, 0, item)
  })
}

/** The keys of the mapping at the path, in file order (empty when there is none). */
export function keysAt(text: string, path: YamlPath): string[] {
  const doc = parseDocument(text)
  const node = path.length === 0 ? doc.contents : doc.getIn(path, true)
  return isMap(node) ? node.items.map((p) => (isScalar(p.key) ? String(p.key.value) : String(p.key))) : []
}

/** A value as the block of YAML a person would write it, for the raw editor of a part no form covers. */
export function toYaml(value: unknown): string {
  return value === undefined ? '' : stringify(value, { lineWidth: 0 }).trimEnd()
}
