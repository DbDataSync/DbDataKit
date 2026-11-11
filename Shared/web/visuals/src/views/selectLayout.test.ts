import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { validateViewsDocument } from './document'
import { selectLayout } from './selectLayout'
import type { LayoutContext, SelectableDocument } from './selectLayout'

/** The goldens shared with the .NET suite (`LayoutSelector`): `Shared/conformance/views/select-layout`. */
const root = join(import.meta.dirname, '../../../../conformance/views')
const read = (relative: string) => JSON.parse(readFileSync(join(root, relative), 'utf8')) as Record<string, any>

describe('selectLayout', () => {
  const golden = read('select-layout/cases.json').cases as { doc: string; name: string; context: LayoutContext; layout?: string; choice?: string; refused?: string }[]
  for (const c of golden)
    it(c.name, () => {
      const doc = read(`select-layout/${c.doc}`) as SelectableDocument
      expect(validateViewsDocument(doc).filter((i) => i.severity === 'error')).toEqual([])
      const selection = selectLayout(doc, c.context)
      if (c.refused !== undefined) {
        expect(selection).toEqual({ ok: false, refusal: c.refused })
        return
      }
      expect(selection.ok).toBe(true)
      if (selection.ok) {
        expect(selection.layout.name).toBe(c.layout)
        expect(selection.choice).toBe(c.choice)
      }
    })
})
