import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'

/** The semantic library's own conformance data: the view fixtures (a view document, a result, what must come of them) and the rendered output of each, recorded by the .NET suite. */
const conformance = join(import.meta.dirname, '../../../../tests/DbDataKit.Semantic.Tests/conformance')

export interface ViewFixture {
  name: string
  file: string
  view: Record<string, unknown>
  expect: { status: 'complete' | 'failed'; errors?: string[]; errors_include?: string[] }
  /** What the .NET `ViewRenderer` gives for this fixture (`conformance/view-rendered`). */
  rendered: { kind: string; status: string; views: Array<Record<string, any>> | null; errors: Array<Record<string, any>> | null }
}

export const semanticSchemaPath = join(import.meta.dirname, '../../../../src/DbDataKit.Semantic/schemas/view.schema.json')

export function viewFixtures(): ViewFixture[] {
  return readdirSync(join(conformance, 'view-fixtures'))
    .filter((f) => f.endsWith('.json'))
    .sort()
    .map((file) => {
      const fixture = JSON.parse(readFileSync(join(conformance, 'view-fixtures', file), 'utf8'))
      const rendered = JSON.parse(readFileSync(join(conformance, 'view-rendered', file), 'utf8'))
      return { name: file.replace(/\.json$/, ''), file, view: fixture.view, expect: fixture.expect, rendered }
    })
}
