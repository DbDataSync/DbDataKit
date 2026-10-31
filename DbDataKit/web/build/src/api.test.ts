import { afterEach, describe, expect, it, vi } from 'vitest'
import { buildApi } from './api'

interface Call { url: string; method: string; body: unknown }

function serve(answer: unknown = {}) {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(init.body as string) : undefined })
    return new Response(JSON.stringify(answer), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }))
  return calls
}

afterEach(() => vi.unstubAllGlobals())

describe('the calls the new-project, new-model and import screens make', () => {
  it('asks for the templates and makes a project from one', async () => {
    const calls = serve({ id: 'shop' })
    await buildApi.templates()
    await buildApi.createProject('shop', 'retail')
    expect(calls).toEqual([
      { url: '/api/v1/templates', method: 'GET', body: undefined },
      { url: '/api/v1/projects', method: 'POST', body: { id: 'shop', template: 'retail' } },
    ])
  })

  it('makes a model in the workspace with the kind and, when chosen, the connections', async () => {
    const calls = serve({ exit: 0, document: { ok: true, data: {} } })
    await buildApi.createModel('shop', 'ada-draft', 'marts.fct_returns', 'view', [])
    await buildApi.createModel('shop', 'ada-draft', 'marts.fct_returns', 'full', ['postgres'])
    expect(calls[0]).toEqual({ url: '/api/v1/projects/shop/commands/project_model_create?workspace=ada-draft', method: 'POST', body: { model: 'marts.fct_returns', kind: 'view' } })
    expect(calls[1].body).toEqual({ model: 'marts.fct_returns', kind: 'full', connection: ['postgres'] })
  })

  it('updates a definition offline, with the answers file once there is one', async () => {
    const calls = serve({ exit: 0, document: { ok: true, data: {} } })
    await buildApi.updateDefinition('shop', 'ada-draft', 'models/marts/fct_returns.sql')
    await buildApi.updateDefinition('shop', 'ada-draft', 'models/marts/fct_returns.sql', '.dbdatabuild/a.yml')
    expect(calls[0].url).toBe('/api/v1/projects/shop/commands/project_model_update?workspace=ada-draft')
    expect(calls[0].body).toEqual({ paths: ['models/marts/fct_returns.sql'], write: true, offline: true })
    expect(calls[1].body).toEqual({ paths: ['models/marts/fct_returns.sql'], write: true, offline: true, answers: '.dbdatabuild/a.yml' })
  })

  it('saves the answers as a new file in the workspace, and says where', async () => {
    const calls = serve({ exit: 0, document: { ok: true, data: { path: 'x' } } })
    const path = await buildApi.writeAnswers('shop', 'ada-draft', [{ id: 'Q-define-a.b-name', accept: 'inferred' }])
    expect(path).toMatch(/^\.dbdatabuild\/console-answers-\d+\.yml$/)
    expect(calls[0].url).toBe('/api/v1/projects/shop/commands/project_file_write?workspace=ada-draft')
    expect(calls[0].body).toEqual({ path, content: 'answers:\n  - id: Q-define-a.b-name\n    accept: inferred\n', create: true })
  })

  it('compiles the draft for real with an empty body, and a check never writes', async () => {
    const calls = serve({ exit: 0, document: { ok: true, data: {} } })
    await buildApi.compileDraft('shop', 'ada-draft')
    await buildApi.checkDraft('shop', 'ada-draft')
    expect(calls[0]).toEqual({ url: '/api/v1/projects/shop/commands/project_compile?workspace=ada-draft', method: 'POST', body: {} })
    expect(calls[1].body).toEqual({ check: true })
  })

  it('lists a connection’s tables without writing, and writes only the chosen ones', async () => {
    const calls = serve({ exit: 0, document: { ok: true, data: {} } })
    await buildApi.importPreview('shop', 'ada-draft', 'origin')
    await buildApi.importTables('shop', 'ada-draft', 'origin', ['shop.orders'])
    expect(calls[0].body).toEqual({ connection: 'origin', tables: ['*.*'] })
    expect(calls[1].body).toEqual({ connection: 'origin', tables: ['shop.orders'], write: true })
    expect(calls.every((c) => c.url === '/api/v1/projects/shop/commands/project_import?workspace=ada-draft')).toBe(true)
  })

  it('reads the project’s connection names from its metadata, and its commands with who may run them', async () => {
    serve({ exit: 0, document: { ok: true, data: { project: { config: { connections: { sqlserver: { engine: 'sqlserver' }, origin: { engine: 'duckdb' } } } } } } })
    expect(await buildApi.connections('shop')).toEqual(['origin', 'sqlserver'])
    serve({ commands: [{ tool: 'project_model_create', name: 'project model create', role: 'author', allowed: false }] })
    expect((await buildApi.commands('shop'))[0].allowed).toBe(false)
  })
})
