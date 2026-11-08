import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import type { FixtureBundle } from '../client/fixtureClient'
import type { RenderedWidget } from '../views/rendered'

/** The sample bundle of plan 32 section 9a, read from disk (node only, for tests): the retail template's two documents and `conformance/views/widget-results`. */
const repo = join(import.meta.dirname, '../../../../..')
export const sampleDocuments = {
  'dashboard/retail-sales-today': join(repo, 'DbDataBuild/templates/retail/dashboards/retail-sales-today.yaml'),
  'report/retail-sales-monthly': join(repo, 'DbDataBuild/templates/retail/reports/retail-sales-monthly.yaml'),
} as const
export const widgetResultsRoot = join(repo, 'DbDataKit/conformance/views/widget-results')

export function sampleBundle(): FixtureBundle {
  const documents: Record<string, string> = {}
  for (const [key, file] of Object.entries(sampleDocuments)) documents[key] = readFileSync(file, 'utf8')
  const results: Record<string, Record<string, RenderedWidget>> = {}
  for (const kind of readdirSync(widgetResultsRoot, { withFileTypes: true }).filter((d) => d.isDirectory()))
    for (const doc of readdirSync(join(widgetResultsRoot, kind.name), { withFileTypes: true }).filter((d) => d.isDirectory())) {
      const entries: Record<string, RenderedWidget> = {}
      for (const file of readdirSync(join(widgetResultsRoot, kind.name, doc.name)).filter((f) => f.endsWith('.json')).sort())
        entries[file.replace(/\.json$/, '')] = JSON.parse(readFileSync(join(widgetResultsRoot, kind.name, doc.name, file), 'utf8')) as RenderedWidget
      results[`${kind.name}/${doc.name}`] = entries
    }
  return { documents, results }
}
