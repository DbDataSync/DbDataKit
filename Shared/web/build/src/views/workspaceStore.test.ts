import { describe, expect, it } from 'vitest'
import { ViewsClientError, clientStore, draftFromOpened, saveDraft, edit, type ViewsClient } from '@dbdatakit/visuals'
import { FileRefusal, FOLDERS, buildFilesApi, semanticModelName, viewFileOf, viewFiles, workspaceViewsClient, type FilesApi } from './workspaceStore'
import type { CommandAnswer } from '../types'

const DASHBOARD = `schemaVersion: 1
kind: dashboard
title: Sales today
widgets:
  note:
    type: text
    text: Hello
layouts:
  - name: desk
    type: reflow
    default: true
    grid: { columns: 12 }
    tiles:
      - { widget: note, x: 0, y: 0, w: 4, h: 2 }
`

/** A workspace's files in memory, with Build's rules: a hash is the version, a stale hash is DDB-471, a file that exists cannot be created. */
function memory(initial: Record<string, string> = {}) {
  const files = new Map(Object.entries(initial))
  let n = 0
  const hashes = new Map<string, string>()
  const hashOf = (path: string) => hashes.get(path)!
  const stamp = (path: string) => { hashes.set(path, `h${++n}`); return hashes.get(path)! }
  for (const path of files.keys()) stamp(path)
  const calls: string[] = []
  const api: FilesApi = {
    async list() { calls.push('list'); return [...files.keys()].map((path) => ({ path, size: files.get(path)!.length, hash: hashOf(path) })) },
    async read(path) { return files.has(path) ? { content: files.get(path)!, hash: hashOf(path) } : null },
    async create(path, content) { calls.push(`create ${path}`); if (files.has(path)) throw new FileRefusal('DDB-471', '--create, but the file exists already'); files.set(path, content); return { hash: stamp(path) } },
    async write(path, content, hash) { calls.push(`write ${path}`); if (hashOf(path) !== hash) throw new FileRefusal('DDB-471', `the file's hash is ${hashOf(path)}, not ${hash}`); files.set(path, content); return { hash: stamp(path) } },
    async remove(path, hash) { calls.push(`remove ${path}`); if (hashOf(path) !== hash) throw new FileRefusal('DDB-471', 'stale'); files.delete(path) },
  }
  return { api, files, calls, touch: (path: string, content: string) => { files.set(path, content); stamp(path) } }
}

const failure = async (p: Promise<unknown>) => { try { await p } catch (e) { return e as ViewsClientError } throw new Error('it did not fail') }

describe('the project paths of documents', () => {
  it('knows the kind and name of a file of dashboards/ or reports/ in any of the three extensions, and nothing else', () => {
    expect(viewFileOf('dashboards/a.yaml')).toEqual({ kind: 'dashboard', name: 'a', path: 'dashboards/a.yaml' })
    expect(viewFileOf('reports/b.yml')?.kind).toBe('report')
    expect(viewFileOf('dashboards/c.json')?.name).toBe('c')
    for (const other of ['dashboards/sub/a.yaml', 'dashboards/a.txt', 'models/a.yaml', 'a.yaml', 'dashboardsx/a.yaml']) expect(viewFileOf(other)).toBeNull()
    expect(FOLDERS).toEqual({ dashboard: 'dashboards', report: 'reports' })
  })

  it('lists the documents of a tree dashboards first, each kind by name', () => {
    const files = ['reports/z.yaml', 'models/x.sql', 'dashboards/b.yaml', 'dashboards/a.yml'].map((path) => ({ path, size: 1, hash: 'h' }))
    expect(viewFiles(files).map((f) => f.path)).toEqual(['dashboards/a.yml', 'dashboards/b.yaml', 'reports/z.yaml'])
  })

  it('reads the name of a semantic model from the top of its file', () => {
    expect(semanticModelName('# retail\nname: retail_sales\nsources: {}\n')).toBe('retail_sales')
    expect(semanticModelName("name: 'orders'  # the model\n")).toBe('orders')
    expect(semanticModelName('sources:\n  name: nested\n')).toBeNull()
  })
})

describe('the workspace store', () => {
  it('lists and opens the documents of the folders with the loader\'s diagnostics and the file\'s hash as the version', async () => {
    const m = memory({ 'dashboards/sales.yaml': DASHBOARD, 'reports/bad.yaml': 'schemaVersion: 1\nkind: report\n', 'models/x.sql': 'select 1' })
    const client = workspaceViewsClient(m.api)
    expect(await client.list('dashboard')).toEqual([{ name: 'sales', kind: 'dashboard', title: 'Sales today' }])
    expect((await client.list()).map((d) => `${d.kind}/${d.name}`)).toEqual(['dashboard/sales', 'report/bad'])
    expect((await client.list('report'))[0]!.title).toBe('bad')    // a file that does not load is listed under its name
    const opened = await client.open('dashboard', 'sales')
    expect(opened.text).toBe(DASHBOARD)
    expect(opened.version).toBe('h1')
    expect(opened.document?.title).toBe('Sales today')
    const broken = await client.open('report', 'bad')
    expect(broken.document).toBeNull()
    expect(broken.diagnostics.length).toBeGreaterThan(0)
    expect(broken.diagnostics[0]!.file).toBe('reports/bad.yaml')
    expect((await failure(client.open('dashboard', 'none'))).code).toBe('not_found')
  })

  it('writes dashboards/<name>.yaml for a new document and the file is the text saved, byte for byte', async () => {
    const m = memory()
    const client = workspaceViewsClient(m.api)
    const saved = await client.save('dashboard', 'sales-today', DASHBOARD)
    expect(m.files.get('dashboards/sales-today.yaml')).toBe(DASHBOARD)
    expect(saved).toEqual({ name: 'sales-today', version: 'h1' })
    await client.save('report', 'monthly', DASHBOARD.replace('dashboard', 'report'))
    expect(m.files.has('reports/monthly.yaml')).toBe(true)
  })

  it('saves over the version it was opened at and refuses a stale save naming the stored version', async () => {
    const m = memory({ 'dashboards/sales.yaml': DASHBOARD })
    const client = workspaceViewsClient(m.api)
    const opened = await client.open('dashboard', 'sales')
    expect(await client.save('dashboard', 'sales', DASHBOARD + '# a\n', opened.version)).toEqual({ name: 'sales', version: 'h2' })
    const stale = await failure(client.save('dashboard', 'sales', DASHBOARD + '# b\n', opened.version))
    expect(stale.code).toBe('version_conflict')
    expect(stale.message).toContain('stored version is h2')
    expect(stale.message).toContain('opened at h1')
    expect(m.files.get('dashboards/sales.yaml')).toBe(DASHBOARD + '# a\n')
  })

  it('refuses a new name that is taken, a save of a document that is gone and a save of an existing one that quotes no version', async () => {
    const m = memory({ 'dashboards/sales.yaml': DASHBOARD })
    const client = workspaceViewsClient(m.api)
    expect((await failure(client.save('dashboard', 'sales', DASHBOARD))).code).toBe('version_conflict')
    expect((await failure(client.save('dashboard', 'gone', DASHBOARD, 'h9'))).code).toBe('not_found')
    m.touch('dashboards/race.yaml', DASHBOARD)
    // the name was free when the list was read and taken when the create arrived: the server's refusal is a conflict too
    const racing: FilesApi = { ...m.api, list: async () => [] }
    expect((await failure(workspaceViewsClient(racing).save('dashboard', 'race', DASHBOARD))).code).toBe('version_conflict')
  })

  it('deletes on the hash it reads and says when there is nothing to delete', async () => {
    const m = memory({ 'dashboards/sales.yaml': DASHBOARD })
    const client = workspaceViewsClient(m.api)
    await client.delete!('dashboard', 'sales')
    expect(m.files.size).toBe(0)
    expect((await failure(client.delete!('dashboard', 'sales'))).code).toBe('not_found')
  })

  it('works as the editor\'s store: a draft saves, then a second draft from the old version is a conflict that the editor can offer to reload', async () => {
    const m = memory({ 'dashboards/sales.yaml': DASHBOARD })
    const store = clientStore(workspaceViewsClient(m.api))
    const first = draftFromOpened(await store.open('dashboard', 'sales'))
    const second = draftFromOpened(await store.open('dashboard', 'sales'))
    const changed = edit(first, { type: 'setTitle', title: 'Sales, changed' }).draft
    const ok = await saveDraft(store, changed)
    expect(ok.status).toBe('saved')
    const stale = await saveDraft(store, edit(second, { type: 'setTitle', title: 'Mine' }).draft)
    expect(stale.status).toBe('conflict')
    expect(m.files.get('dashboards/sales.yaml')).toContain('title: Sales, changed')
  })

  it('checks a text as the loaders do, in the browser', async () => {
    const client = workspaceViewsClient(memory().api)
    expect((await client.validate(DASHBOARD, { kind: 'dashboard' })).ok).toBe(true)
    const bad = await client.validate(DASHBOARD.replace('widget: note', 'widget: nope'), { kind: 'dashboard' })
    expect(bad.ok).toBe(false)
    expect(bad.diagnostics.some((d) => d.path.startsWith('layouts[0].tiles[0]'))).toBe(true)
  })

  it('says how to get rows when no Gateway is chosen, and asks the chosen one otherwise', async () => {
    const m = memory()
    const none = workspaceViewsClient(m.api)
    expect((await failure(none.preview({ widget: { type: 'text', text: 'x' } }))).message).toContain('choose a Gateway')
    expect(none.previewDocument).toBeUndefined()
    const asked: string[] = []
    const rows = { preview: async () => { asked.push('preview'); return { id: 'w', state: 'ready' } }, previewDocument: async () => { asked.push('document'); return { widgets: {} } } } as unknown as ViewsClient
    const some = workspaceViewsClient(m.api, { rows })
    await some.preview({ widget: { type: 'text', text: 'x' } })
    await some.previewDocument!({ kind: 'dashboard', text: DASHBOARD })
    expect(asked).toEqual(['preview', 'document'])
  })

  it('gives the wells the model\'s catalog, from either shape the server answers, and says when a model has none', async () => {
    const m = memory()
    const body = { model: 'retail_sales', catalog: { measures: { 'sales.net_amount': { label: 'Net sales' } }, dimensions: { customers: { attributes: { region: {} } } } } }
    const client = workspaceViewsClient(m.api, { catalog: async () => body })
    const catalog = await client.catalog!('retail_sales')
    expect(catalog.model).toBe('retail_sales')
    expect(catalog.measures.map((x) => x.name)).toEqual(['sales.net_amount'])
    expect(catalog.attributes.map((x) => x.name)).toEqual(['customers.region'])
    const empty = workspaceViewsClient(m.api, { catalog: async () => ({ catalog: {} }) })
    expect((await failure(empty.catalog!('x'))).code).toBe('bad_response')
    expect(workspaceViewsClient(m.api).catalog).toBeUndefined()
  })
})

describe('Build\'s file commands as the store\'s calls', () => {
  const answer = <T,>(ok: boolean, data: T, extra: Partial<NonNullable<CommandAnswer<T>['document']>> = {}): CommandAnswer<T> => ({ exit: ok ? 0 : 1, document: { ok, data, ...extra }, text: null })

  it('turns a refused write into a refusal that carries the server\'s code and words, and a missing file into null', async () => {
    const files = buildFilesApi({
      files: async () => answer(true, { files: [{ path: 'dashboards/a.yaml', size: 1, hash: 'h' }] }),
      read: async () => answer(false, null as never, { errors: ['there is no such file'] }),
      create: async () => answer(true, { hash: 'new' }),
      write: async () => answer(false, null as never, { diagnostics: [{ code: 'DDB-471', severity: 'error', title: 't', location: { file: 'dashboards/a.yaml', line: 0, column: 0 }, found: 'the file\'s hash is x, not y', supported: '', fix: '' }] }),
      remove: async () => ({ exit: 1, document: null, text: 'Not allowed.' }),
    })
    expect((await files.list()).map((f) => f.path)).toEqual(['dashboards/a.yaml'])
    expect(await files.read('dashboards/a.yaml')).toBeNull()
    expect(await files.create('dashboards/b.yaml', '')).toEqual({ hash: 'new' })
    const stale = await files.write('dashboards/a.yaml', 'x', 'y').catch((e: unknown) => e as FileRefusal)
    expect(stale).toBeInstanceOf(FileRefusal)
    expect((stale as FileRefusal).code).toBe('DDB-471')
    expect((stale as FileRefusal).message).toContain('hash is x')
    const denied = await files.remove('dashboards/a.yaml', 'h').catch((e: unknown) => e as FileRefusal)
    expect((denied as FileRefusal).message).toBe('Not allowed.')
  })
})
