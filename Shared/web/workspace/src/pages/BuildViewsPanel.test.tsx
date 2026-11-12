import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { workspaceViewsClient } from '@dbdatakit/build/files'
import { ViewsClientError } from '@dbdatakit/visuals'
import type { CheckDiagnostic } from '../api'
import { draftFilesApi, semanticModelOf, versionOf, viewFindings, type DraftFiles } from '../buildViews'
import { ViewDocumentsView, viewDocumentsOf } from './BuildViewsPanel'

/** A draft in memory: the files by path. */
function memory(initial: Record<string, string> = {}): DraftFiles & { files: Map<string, string> } {
  const files = new Map(Object.entries(initial))
  return {
    files,
    list: async () => [...files].map(([path, text]) => ({ path, size: text.length })),
    read: async (path) => files.get(path) ?? null,
    write: async (path, content) => { files.set(path, content) },
    remove: async (path) => { files.delete(path) },
  }
}

const dashboard = 'schema: dashboard/1\ntitle: Sales\nlayouts: {}\nwidgets: {}\n'

describe('the dashboards and reports of a draft', () => {
  it('are the files of dashboards/ and reports/, dashboards first', () => {
    const found = viewDocumentsOf(['reports/b.yaml', 'dashboards/z.yaml', 'dashboards/a.yml', 'models/x.sql', 'dashboards/sub/no.yaml'])
    expect(found.map((d) => `${d.kind}/${d.name}`)).toEqual(['dashboard/a', 'dashboard/z', 'report/b'])
  })
  it('keep the findings of those folders only, and not the notes', () => {
    const found: CheckDiagnostic[] = [
      { severity: 'error', code: 'DDB-480', message: 'x', file: 'dashboards/a.yaml', line: 3 },
      { severity: 'info', code: 'DDB-900', message: 'note', file: 'dashboards/a.yaml', line: null },
      { severity: 'error', code: 'DDB-301', message: 'y', file: 'models/a.sql', line: 1 },
      { severity: 'warning', code: 'DDB-481', message: 'z', file: null, line: null },
    ]
    expect(viewFindings(found).map((d) => d.code)).toEqual(['DDB-480'])
  })
  it('are listed with their findings and the buttons for a new one', () => {
    const docs = viewDocumentsOf(['dashboards/a.yaml', 'reports/r.yaml'])
    const html = renderToStaticMarkup(<ViewDocumentsView documents={docs} findings={[{ severity: 'error', code: 'DDB-480', message: 'x', file: 'dashboards/a.yaml', line: 3 }]} canWrite onEdit={() => {}} onNew={() => {}} />)
    expect((html.match(/data-testid="wsp-views-doc"/g) ?? []).length).toBe(2)
    expect(html).toContain('data-name="a" data-errors="1"')
    expect(html).toContain('data-testid="wsp-views-new-report"')
    expect(renderToStaticMarkup(<ViewDocumentsView documents={[]} findings={null} canWrite={false} onEdit={() => {}} onNew={() => {}} />)).toContain('data-testid="wsp-views-empty"')
  })
})

describe('the views client over a draft', () => {
  it('saves a new dashboard as a file of the draft and opens it again at the version it holds', async () => {
    const draft = memory()
    const client = workspaceViewsClient(draftFilesApi(draft))
    const saved = await client.save('dashboard', 'sales', dashboard)
    expect(draft.files.get('dashboards/sales.yaml')).toBe(dashboard)
    const opened = await client.open('dashboard', 'sales')
    expect(opened.version).toBe(saved.version)
    expect(opened.version).toBe(await versionOf(dashboard))
    expect((await client.list('dashboard')).map((d) => d.name)).toEqual(['sales'])
  })
  it('refuses a stale save, naming the stored version, and a new name that is taken', async () => {
    const draft = memory({ 'dashboards/sales.yaml': dashboard })
    const client = workspaceViewsClient(draftFilesApi(draft))
    const opened = await client.open('dashboard', 'sales')
    draft.files.set('dashboards/sales.yaml', dashboard + '# edited elsewhere\n')
    await expect(client.save('dashboard', 'sales', dashboard + '# mine\n', opened.version)).rejects.toMatchObject({ code: 'version_conflict' })
    await expect(client.save('dashboard', 'sales', dashboard)).rejects.toBeInstanceOf(ViewsClientError)
    expect(draft.files.get('dashboards/sales.yaml')).toContain('edited elsewhere')
  })
  it('deletes a document and says when there is none', async () => {
    const draft = memory({ 'reports/q.yaml': dashboard })
    const client = workspaceViewsClient(draftFilesApi(draft))
    await client.delete('report', 'q')
    expect(draft.files.has('reports/q.yaml')).toBe(false)
    await expect(client.open('report', 'q')).rejects.toMatchObject({ code: 'not_found' })
  })
})

const semanticFile = `version: "0.3"
name: retail_sales
measures:
  sales.net_amount: {type: number, label: Net sales, bindings: {default: {table: marts.fct, column: net}}}
dimensions:
  customers:
    attributes:
      region: {type: string, synonyms: [customer region]}
      signed_up: {type: date, time: true}
`

describe('the model catalog of the wells, from the draft\'s own semantic files', () => {
  it('gives the editor the measures and the dimension attributes the file declares, named as a query names them', async () => {
    const draft = memory({ 'semantic/sales.yml': semanticFile, 'semantic/other.yml': 'name: other\nmeasures: {x.y: {}}\n' })
    const client = workspaceViewsClient(draftFilesApi(draft), { catalog: (model) => semanticModelOf(draft, [...draft.files.keys()], model) })
    const catalog = await client.catalog!('retail_sales')
    expect(catalog.model).toBe('retail_sales')
    expect(catalog.measures.map((m) => [m.name, m.label])).toEqual([['sales.net_amount', 'Net sales']])
    expect(catalog.attributes.map((a) => a.name)).toEqual(['customers.region', 'customers.signed_up'])
    expect(catalog.attributes[0]!.label).toBe('customer region')
    expect(catalog.attributes[1]!.time).toBe(true)
  })
  it('says which model is missing and which file is not YAML, and a file with no members has no catalog', async () => {
    const draft = memory({ 'semantic/bad.yml': 'name: bad\nmeasures: [unclosed\n', 'semantic/empty.yml': 'name: empty\n' })
    const paths = [...draft.files.keys()]
    await expect(semanticModelOf(draft, paths, 'nope')).rejects.toThrow("no semantic model named 'nope'")
    await expect(semanticModelOf(draft, paths, 'bad')).rejects.toThrow('semantic/bad.yml is not valid YAML')
    const client = workspaceViewsClient(draftFilesApi(draft), { catalog: (model) => semanticModelOf(draft, paths, model) })
    await expect(client.catalog!('empty')).rejects.toMatchObject({ code: 'bad_response' })
  })
})
