import { parseDocument, LineCounter, isMap, isNode } from 'yaml'
import { validateViewsDocument, type ViewKind } from '../views/document'
import type { ViewsDoc } from '../views/model'
import type { ViewsIssue } from '../views/common'
import type { ClientDiagnostic, ValidationResult } from './types'

/** A path as `layouts[1].tiles[0].x` into the keys and indices YAML nodes are found by. */
export function pathSteps(path: string): Array<string | number> {
  const steps: Array<string | number> = []
  for (const m of path.matchAll(/([^.[\]]+)|\[(\d+)\]/g)) steps.push(m[2] !== undefined ? Number(m[2]) : m[1]!)
  return steps
}

/**
 * Parses a YAML or JSON file and validates it as a dashboard or a report with the TypeScript validator (the same checks, codes and paths as the .NET loaders), placing each diagnostic in the text.
 * A syntax error or a duplicate key stops before validation. A path that names a key the file does not have (a missing key) is placed at the nearest parent that exists.
 */
export function validateText(text: string, options: { kind?: ViewKind; file?: string } = {}): ValidationResult {
  const file = options.file
  const lines = new LineCounter()
  const doc = parseDocument(text, { lineCounter: lines, prettyErrors: false, uniqueKeys: true })
  const place = (code: string, severity: 'error' | 'warning', path: string, message: string, offset?: number): ClientDiagnostic => {
    const diagnostic: ClientDiagnostic = { code, severity, path, message, ...(file ? { file } : {}) }
    if (offset !== undefined) {
      const pos = lines.linePos(offset)
      diagnostic.line = pos.line
      diagnostic.column = pos.col
    }
    return diagnostic
  }
  if (doc.errors.length > 0) {
    const diagnostics = doc.errors.map((e) => place(e.code === 'DUPLICATE_KEY' ? 'DBD-102' : 'DBD-101', 'error', '', e.message.split('\n')[0] ?? e.message, e.pos[0]))
    return { ok: false, document: null, diagnostics }
  }
  const value = doc.toJS() as unknown
  if (value === null || value === undefined) return { ok: false, document: null, diagnostics: [place('DBD-106', 'error', '', 'The file is empty: a dashboard or a report is a mapping with `schemaVersion`, `kind`, `title`, `widgets` and `layouts`.', 0)] }

  const issues: ViewsIssue[] = validateViewsDocument(value, { kind: options.kind, file })
  const offsetOf = (path: string): number => {
    let steps = pathSteps(path)
    for (; ;) {
      const node = steps.length === 0 ? doc.contents : doc.getIn(steps, true)
      if (isNode(node) && node.range) {
        // a key's own position when the path ends at a map entry, else the value's
        if (steps.length > 0) {
          const parent = steps.length === 1 ? doc.contents : doc.getIn(steps.slice(0, -1), true)
          const last = steps[steps.length - 1]
          if (isMap(parent) && typeof last === 'string') {
            const pair = parent.items.find((p) => isNode(p.key) && String((p.key as { value: unknown }).value) === last)
            if (pair && isNode(pair.key) && pair.key.range) return pair.key.range[0]
          }
        }
        return node.range[0]
      }
      if (steps.length === 0) return 0
      steps = steps.slice(0, -1)
    }
  }
  const diagnostics = issues.map((i) => place(i.code, i.severity, i.path, i.message, offsetOf(i.path)))
  const failed = issues.some((i) => i.severity === 'error')
  return { ok: !failed, document: failed ? null : (value as ViewsDoc), diagnostics }
}

/** A short deterministic hash of a text (FNV-1a, 32 bit, hex): the version a fixture store quotes. Not a security measure. */
export function textVersion(text: string): string {
  let h = 0x811c9dc5
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i)
    h = Math.imul(h, 0x01000193) >>> 0
  }
  return h.toString(16).padStart(8, '0')
}
