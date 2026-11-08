import { isMap, isScalar, isSeq, parseDocument, type Document } from 'yaml'
import { alwaysBlock, flowOk, keyOrderAt, styleNode } from './canonical'

/** Where a value is in a document: keys of mappings and positions in lists. */
export type Path = Array<string | number>

/** One change to the text of a file. The editor's commands compile to these (`commandOps`); each keeps comments, key order and the style of everything it does not touch. */
export type Op =
  | { op: 'set'; path: Path; value: unknown }
  | { op: 'remove'; path: Path }
  | { op: 'insert'; path: Path; index: number; value: unknown }
  | { op: 'move'; path: Path; from: number; to: number }
  | { op: 'renameKey'; path: Path; from: string; to: string }

export class YamlEditError extends Error {}

const isPlain = (value: unknown): value is string | number | boolean | null => value === null || ['string', 'number', 'boolean'].includes(typeof value)

function create(doc: Document, value: unknown, path: Path) {
  const node = doc.createNode(value)
  styleNode(node, path)
  return node
}

/** A collection written on one line that has grown too long, or now holds a block (an empty `widgets: {}` that got its first widget), becomes a block; the ones that still fit stay as they were. */
function restyleAlong(doc: Document, path: Path): void {
  for (let i = 0; i <= path.length; i++) {
    const at = path.slice(0, i)
    const node = at.length === 0 ? doc.contents : doc.getIn(at, true)
    if ((isMap(node) || isSeq(node)) && node.flow && (alwaysBlock(at) || !flowOk(node.toJSON()))) node.flow = false
  }
}

function apply(doc: Document, op: Op): void {
  applyOne(doc, op)
  if (op.op !== 'remove') restyleAlong(doc, op.op === 'set' ? op.path : op.path)
}

function applyOne(doc: Document, op: Op): void {
  switch (op.op) {
    case 'set': {
      const existing = op.path.length === 0 ? doc.contents : doc.getIn(op.path, true)
      if (isScalar(existing) && isPlain(op.value) && typeof existing.value !== 'object') { existing.value = op.value; return }
      if (op.path.length === 0) { doc.contents = create(doc, op.value, []) as never; return }
      // a missing parent is made as a mapping, or a list when the next step is a position
      for (let i = 1; i < op.path.length; i++) if (!doc.hasIn(op.path.slice(0, i))) applyOne(doc, { op: 'set', path: op.path.slice(0, i), value: typeof op.path[i] === 'number' ? [] : {} })
      const parent = doc.getIn(op.path.slice(0, -1), true)
      const key = op.path[op.path.length - 1]
      const order = keyOrderAt(op.path.slice(0, -1))
      // a new key of a mapping with a canonical order goes where that order puts it, so a document edited by commands stays in the order `canonicalText` writes
      if (order && isMap(parent) && typeof key === 'string' && !parent.has(key) && order.includes(key)) {
        const rank = order.indexOf(key)
        const after = parent.items.reduce((at, pair, i) => (isScalar(pair.key) && order.includes(String(pair.key.value)) && order.indexOf(String(pair.key.value)) < rank ? i + 1 : at), 0)
        parent.items.splice(after, 0, doc.createPair(key, create(doc, op.value, op.path)))
        return
      }
      doc.setIn(op.path, create(doc, op.value, op.path))
      return
    }
    case 'remove':
      if (!doc.deleteIn(op.path)) throw new YamlEditError(`There is nothing at \`${op.path.join('.')}\` to remove.`)
      return
    case 'insert': {
      const seq = op.path.length === 0 ? doc.contents : doc.getIn(op.path, true)
      if (!isSeq(seq)) throw new YamlEditError(`\`${op.path.join('.')}\` is not a list.`)
      seq.items.splice(op.index, 0, create(doc, op.value, [...op.path, op.index]) as never)
      return
    }
    case 'move': {
      const seq = doc.getIn(op.path, true)
      if (!isSeq(seq)) throw new YamlEditError(`\`${op.path.join('.')}\` is not a list.`)
      const [item] = seq.items.splice(op.from, 1)
      seq.items.splice(op.to, 0, item!)
      return
    }
    case 'renameKey': {
      const map = op.path.length === 0 ? doc.contents : doc.getIn(op.path, true)
      if (!isMap(map)) throw new YamlEditError(`\`${op.path.join('.')}\` is not a mapping.`)
      const pair = map.items.find((p) => isScalar(p.key) && String(p.key.value) === op.from)
      if (!pair || !isScalar(pair.key)) throw new YamlEditError(`There is no key \`${op.from}\`.`)
      pair.key.value = op.to
    }
  }
}

/** The text with the changes applied in order. Throws `YamlEditError` when the text is not YAML or a change does not fit it. */
export function applyOps(text: string, ops: Op[]): string {
  if (ops.length === 0) return text
  const doc = parseDocument(text)
  if (doc.errors.length > 0) throw new YamlEditError('The text is not valid YAML, so a form cannot change it. Fix it in the YAML view first.')
  if (doc.contents == null) doc.contents = doc.createNode({}) as never
  for (const op of ops) apply(doc, op)
  return doc.toString({ lineWidth: 0, flowCollectionPadding: false })
}
