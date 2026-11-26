// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { parseYaml } from '@dbdatakit/shell'
import { WorkspaceModule, commitsAfter, errorsUnder, registerDesigner } from '@dbdatakit/workspace'
import { syncDesigner } from './designer'
import { PLAN_SETTLE_MS } from './api/hooks'
import { overviewTabOf } from './pages/OverviewPanel'
import { tabOf } from './pages/ReplicationPage'
import { PipelineView, endpointWords, withStage } from './replication/PipelineCard'
import { stagedLine } from './replication/ProvisioningTab'
import { publishStateOf } from './replication/PublishCard'
import { unpublishedOf } from './components/UnpublishedChanges'
import type { DriverCapabilities } from './api/types'
import type { ReplicationTaskConfig } from './types'
import { DRAFT } from './fixtures'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const SQLSERVER: DriverCapabilities = {
  driverType: 'MsSql', supportsConnectionTest: true, supportedProvisioningActions: [],
  readers: [
    { kind: 'MsSqlChangeTracking', parameters: [], supportsSegmentation: false, detectsDeletes: true, supportedIntents: ['Changes'], needsKey: 'SourceKey' },
    { kind: 'Watermark', parameters: [], supportsSegmentation: false, detectsDeletes: false, detectsKeyChanges: false, supportedIntents: ['Changes'] },
    { kind: 'BatchReload', parameters: [], supportsSegmentation: true, detectsDeletes: false, supportedIntents: [] },
  ],
  stagingProviders: [{ kind: 'MsSqlStagingTable', parameters: [] }],
  writers: [],
}
const POSTGRES: DriverCapabilities = {
  driverType: 'Postgres', supportsConnectionTest: true, supportedProvisioningActions: [], readers: [],
  stagingProviders: [{ kind: 'PgCopyStaging', parameters: [] }, { kind: 'StagingTable', parameters: [] }],
  writers: [
    { kind: 'ChangeApply', parameters: [], supportsReconciliation: false, needsKey: 'TargetKey' },
    { kind: 'DeleteInsert', parameters: [], supportsReconciliation: true },
    { kind: 'Scd2', parameters: [{ name: 'naturalKey', type: 'Text' } as never], supportsReconciliation: false, needsKey: 'NaturalKey' },
  ],
}

const PLAN = {
  ok: true, diagnostics: [], data: {
    replication: 'orders', cache_generated_at: '2026-11-20T10:00:00Z', cache_server: 'runners-eu', mappings: [{
      mapping: 'dbo.orders', source: { connection: 'erp', table: 'dbo.orders' }, target: { connection: 'dw', table: 'dbo.orders' }, excluded: null,
      source_plan: { action: 'enableSourceChangeCapture', state: 'satisfied', steps: [], warnings: [] },
      target_plan: { action: 'createTargetTable', state: 'missing', steps: [{ title: 'Create the table', command_text: 'CREATE TABLE "dbo"."orders" ("id" integer)', rationale: 'it is not there', scope: 'table' }], warnings: [] },
    }],
  },
}

/** A Workspace server in memory with a sync draft: the frame's file calls, the check (with an error in one replication), the sync routes and the plan. */
function fakeServer(files: Record<string, string>) {
  const writes: string[] = []
  const calls: string[] = []
  const plans: { replication: string; unsaved?: Record<string, string | null> }[] = []
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = decodeURIComponent(url.pathname)
    calls.push(`${init?.method ?? 'GET'} ${path}`)
    if (path === '/api/auth/status') return reply({ authenticated: true, role: 'Admin', name: 'ana' })
    if (path === '/api/v1/projects') return reply({ projects: [{ id: 'plant', kind: 'sync', title: null, created: '', workspaces: 1 }] })
    const m = /^\/api\/v1\/projects\/([^/]+)\/workspaces(?:\/([^/]+)(?:\/(.*))?)?$/.exec(path)
    if (!m) return reply({ error: 'no' }, 404)
    const [, project, id, rest] = m
    if (!id) return reply({ workspaces: [{ id: 'ws-p', project, name: 'edit', branch: 'workspace/edit', base: 'abc', created: '', owner: 'ana', mine: true }] })
    if (rest === 'files') return reply({ files: Object.keys(files).sort().map((p) => ({ path: p, size: files[p].length })), truncated: false, max_file_bytes: 1 })
    if (rest?.startsWith('files/')) {
      const p = rest.slice('files/'.length)
      if (init?.method === 'PUT') { files[p] = (JSON.parse(String(init.body)) as { content: string }).content; writes.push(p); return reply({ path: p, created: false, size: 1 }) }
      return p in files ? reply({ path: p, content: files[p] }) : reply({ error: 'none' }, 404)
    }
    if (rest === 'check') return reply({ kind: 'sync', ok: false, diagnostics: [
      { severity: 'error', code: 'DBD-SYNC-X', message: 'one', file: 'config/replications/invoices/task.yaml', line: 3 },
      { severity: 'error', code: 'DBD-SYNC-Y', message: 'two', file: 'config/replications/invoices/table-mappings/a.yaml', line: 1 },
      { severity: 'warning', code: 'DBD-SYNC-W', message: 'not an error', file: 'config/replications/orders/task.yaml', line: 1 },
    ] })
    if (rest === 'changes') return reply({ changes: [], uncommitted: false })
    if (rest === 'history') return reply({ commits: [] })
    if (rest === 'events') return reply({ events: [] })
    if (rest === 'provisioning-plan') { plans.push(JSON.parse(String(init?.body)) as { replication: string; unsaved?: Record<string, string | null> }); return reply(PLAN) }
    if (rest === 'sync/script-slots') return reply({ slots: [{ slot: 'rowTransform', levels: ['connection', 'replication', 'mapping'], label: 'Row transform', description: 'Transforms a whole row.' }] })
    if (rest === 'sync/capabilities') {
      const connection = (JSON.parse(String(init?.body ?? '{}')) as { connection: string }).connection
      return reply(connection === 'erp' ? SQLSERVER : POSTGRES)
    }
    return reply({ error: 'no' }, 404)
  })
  return { files, writes, calls, plans, fetcher }
}

let root: Root
let host: HTMLDivElement
let where = ''
function Where() { where = useLocation().pathname; return null }
const settle = async () => { for (let i = 0; i < 12; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null
const all = (sel: string) => [...host.querySelectorAll(sel)] as HTMLElement[]
const click = async (sel: string) => { const el = q(sel); if (!el) throw new Error(`no ${sel} in ${host.innerHTML.slice(0, 3000)}`); await act(async () => { el.click() }); await settle() }
async function choose(sel: string, value: string) {
  const el = q(sel) as HTMLSelectElement | null
  if (!el) throw new Error(`no ${sel}`)
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')!.set!.call(el, value)
    el.dispatchEvent(new Event('change', { bubbles: true }))
  })
  await settle()
}
async function type(sel: string, value: string) {
  const el = q(sel) as HTMLInputElement | HTMLTextAreaElement | null
  if (!el) throw new Error(`no ${sel}`)
  const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype
  await act(async () => {
    Object.getOwnPropertyDescriptor(proto, 'value')!.set!.call(el, value)
    el.dispatchEvent(new Event('input', { bubbles: true }))
  })
  await settle()
}

async function render(at: string) {
  host = document.createElement('div')
  document.body.appendChild(host)
  root = createRoot(host)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[at]}><Where /><WorkspaceModule /></MemoryRouter></QueryClientProvider>) })
  await settle()
}

const yamlOf = (text: string | undefined) => parseYaml(text ?? '').value as Record<string, any>
const ORDERS = '/plant/ws-p/designer/replications/orders'
const SCRIPT = 'name: trim-names\nkind: rowTransform\nlanguage: CSharp\n'

describe('the rest of a replication\'s page', () => {
  let server: ReturnType<typeof fakeServer>
  beforeEach(() => {
    server = fakeServer({
      ...DRAFT,
      'config/scripts/trim-names.yaml': SCRIPT,
      'config/replications/invoices/task.yaml': 'schemaVersion: 2\nname: invoices\nscheduling: {mode: Continuous, frequencySeconds: 60}\nchangeProcessing:\n  reader: {kind: BatchReload, options: {}}\n  cache: {kind: StagingTable, options: {}}\n  writer: {kind: DeleteInsert, options: {}}\n',
    })
    vi.stubGlobal('fetch', server.fetcher)
    registerDesigner(syncDesigner)
  })
  afterEach(() => { if (root) act(() => root.unmount()); host?.remove(); vi.unstubAllGlobals() })

  it('counts the check\'s errors of each replication in the navigation, and none for a replication with only warnings', async () => {
    await render(ORDERS)
    const errors = all('[data-testid="sd-nav-replication"]').map((a) => [a.dataset.name, a.querySelector('[data-testid="sd-nav-errors"]')?.getAttribute('data-count') ?? null])
    expect(errors).toEqual([['invoices', '2'], ['orders', null]])
  })

  it('opens each sub-tab of the Overview at its own address, with the tracking settings above them', async () => {
    await render(ORDERS)
    expect(q('[data-testid="tracking-card"]')).not.toBeNull()
    expect(q('[data-testid="replication-overview"]')?.dataset.subtab).toBe('notes')
    expect(q('[data-testid="replication-notes-card"]')).not.toBeNull()
    await click('[data-testid="overview-tab-pipeline"]')
    expect(where).toBe(`${ORDERS}/overview/pipeline`)
    expect(q('[data-testid="replication-pipeline"]')).not.toBeNull()
    expect(q('[data-testid="reconcile-config-card"]')).not.toBeNull()
    await click('[data-testid="overview-tab-segmenting"]')
    expect(where).toBe(`${ORDERS}/overview/segmenting`)
    expect(q('[data-testid="segmenting-strategies"]')).not.toBeNull()
    await click('[data-testid="tab-checks"]')
    expect(where).toBe(`${ORDERS}/checks`)
    expect(q('[data-testid="replication-checks"]')).not.toBeNull()
  })

  it('picks a reader from the source driver\'s and a writer from the target driver\'s, as unsaved edits of task.yaml', async () => {
    await render(`${ORDERS}/overview/pipeline`)
    expect(all('[data-testid="reader-kind-select"] option').map((o) => (o as HTMLOptionElement).value)).toEqual(['MsSqlChangeTracking', 'Watermark', 'BatchReload'])
    expect(q('[data-testid="pipeline-drivers"]')?.textContent).toContain('erp')
    await choose('[data-testid="reader-kind-select"]', 'Watermark')
    expect(q('[data-testid="key-change-blind-note"]')).not.toBeNull()
    await click('[data-testid="stage-writer"]')
    expect(all('[data-testid="writer-kind-select"] option').map((o) => (o as HTMLOptionElement).value)).toEqual(['PostgresMerge', 'ChangeApply', 'DeleteInsert', 'Scd2'])
    await choose('[data-testid="writer-kind-select"]', 'Scd2')
    expect(q('[data-testid="scd2-delete-blind-warning"]')).not.toBeNull()
    expect(q('[data-testid="sd-task-dirty"]')).not.toBeNull()
    await click('[data-testid="save-settings-button"]')
    const saved = yamlOf(server.files['config/replications/orders/task.yaml'])
    expect(saved.changeProcessing.reader.kind).toBe('Watermark')
    expect(saved.changeProcessing.writer.kind).toBe('Scd2')
    expect(server.files['config/replications/orders/task.yaml']).toContain('# the nightly orders copy')
  })

  it('binds a script from the slots the Workspace offers on Custom Transforms', async () => {
    await render(`${ORDERS}/overview/transforms`)
    expect(all('[data-testid="script-binding-rowTransform"] option').map((o) => (o as HTMLOptionElement).textContent)).toContain('trim-names')
    await choose('[data-testid="script-binding-rowTransform"]', 'trim-names')
    await click('[data-testid="save-settings-button"]')
    expect(yamlOf(server.files['config/replications/orders/task.yaml']).scripts).toEqual({ rowTransform: { scriptName: 'trim-names', parameters: {} } })
  })

  it('shows the replication\'s plan grouped by connection and side, and no Apply while no build of the draft is staged on a Runner', async () => {
    await render(`${ORDERS}/overview/provisioning`)
    expect(server.calls).toContain('POST /api/v1/projects/plant/workspaces/ws-p/provisioning-plan')
    const group = q('[data-testid="provisioning-group"]')
    expect(group?.dataset.connection).toBe('dw')
    expect(group?.dataset.side).toBe('target')
    expect(q('[data-testid="provisioning-step-sql"]')?.textContent).toBe('CREATE TABLE "dbo"."orders" ("id" integer)')
    expect(q('[data-testid="provisioning-apply"]')).toBeNull()
    expect(q('[data-testid="provisioning-staged"]')?.textContent).toContain('No build of this draft is staged on a Runner yet')
    expect(server.plans[0].unsaved).toBeUndefined()
    await click('[data-testid="task-provisioning-create"] button.toggle')
    await click('[data-testid="task-provisioning-create-checkbox"]')
    expect(q('[data-testid="provisioning-plan-basis"]')?.textContent).toContain('one unsaved edit included')
    // the plan is asked again with the edit once the edits have settled
    await act(async () => { await new Promise((r) => setTimeout(r, PLAN_SETTLE_MS + 20)) })
    await settle()
    const asked = server.plans.at(-1)!.unsaved ?? {}
    expect(Object.keys(asked)).toEqual(['config/replications/orders/task.yaml'])
    expect(yamlOf(asked['config/replications/orders/task.yaml'] ?? '').provisioning.createTargetTableIfMissing).toBe(true)
    expect(server.writes).toEqual([])
  })

  it('adds a freshness check on Quality Checks as an unsaved edit of task.yaml', async () => {
    await render(`${ORDERS}/checks`)
    await click('[data-testid="replication-check-add"]')
    await type('[data-testid="replication-check-id-input"]', 'orders_fresh')
    await type('[data-testid="replication-check-dataset-input"]', 'dbo.orders')
    await type('[data-testid="replication-check-maxAge-input"]', '15m')
    await click('[data-testid="replication-check-apply"]')
    expect(q('[data-testid="replication-check-orders_fresh"]')?.dataset.kind).toBe('freshness')
    expect(q('[data-testid="tab-checks-count"]')?.textContent).toBe('1')
    await click('[data-testid="save-settings-button"]')
    expect(yamlOf(server.files['config/replications/orders/task.yaml']).checks).toEqual([expect.objectContaining({ id: 'orders_fresh', kind: 'freshness', dataset: 'dbo.orders', max_age: '15m' })])
  })
})

describe('the pieces of the page', () => {
  const task: ReplicationTaskConfig = {
    name: 'orders', enabled: true, endpoints: { source: { connectionName: 'erp', database: null }, target: { connectionName: 'dw', database: null } },
    scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
    changeProcessing: { reader: { kind: 'BatchReload', options: {} }, cache: { kind: 'StagingTable', options: {} }, writer: { kind: 'DeleteInsert', options: {} }, degreeOfParallelism: 4, bulkLoadDegreeOfParallelism: 4 },
  }

  it('reads which tab and sub-tab an address opens', () => {
    expect(tabOf('')).toBe('overview')
    expect(tabOf('overview/pipeline')).toBe('overview')
    expect(tabOf('mappings/dbo.orders/columns')).toBe('mappings')
    expect(tabOf('checks')).toBe('checks')
    expect(overviewTabOf('')).toBe('notes')
    expect(overviewTabOf('overview')).toBe('notes')
    expect(overviewTabOf('overview/provisioning')).toBe('provisioning')
    expect(overviewTabOf('overview/nothing')).toBe('notes')
  })

  it('brings the change writer with a reader that reads changes behind a reconciling writer, and leaves a writer the person chose otherwise', () => {
    expect(withStage(task, 'reader', { kind: 'MsSqlChangeTracking', options: {} }, SQLSERVER, POSTGRES).changeProcessing.writer.kind).toBe('ChangeApply')
    const scd2 = { ...task, changeProcessing: { ...task.changeProcessing, writer: { kind: 'Scd2', options: {} } } }
    expect(withStage(scd2, 'reader', { kind: 'MsSqlChangeTracking', options: {} }, SQLSERVER, POSTGRES).changeProcessing.writer.kind).toBe('Scd2')
    expect(withStage(task, 'cache', { kind: 'PgCopyStaging', options: {} }, SQLSERVER, POSTGRES).changeProcessing.cache.kind).toBe('PgCopyStaging')
  })

  it('names a connection with its engine from the registry, and says when none is chosen', () => {
    const engines = { all: [], sources: [], targets: [], find: (n: string) => (n === 'sqlserver' ? { id: 'sqlserver', displayName: 'SQL Server', aliases: [], custom: false, source: { status: 'supported', note: null, offered: true }, target: { status: 'supported', note: null, offered: true } } as never : undefined) }
    const connections = [{ name: 'erp', path: 'config/connections/erp.yaml', engine: 'sqlserver', database: null, scripts: {} }, { name: 'odd', path: '', engine: 'weird', database: null, scripts: {} }]
    expect(endpointWords('erp', connections, engines)).toBe('erp (SQL Server)')
    expect(endpointWords('odd', connections, engines)).toBe('odd (weird)')
    expect(endpointWords(null, connections, engines)).toBe('no connection chosen')
  })

  it('keeps a kind the driver does not offer, named as such, and says which mappings the pipeline applies to', () => {
    const html = renderToStaticMarkup(<PipelineView draft={{ ...task, changeProcessing: { ...task.changeProcessing, reader: { kind: 'Gone', options: {} } } }} setDraft={() => {}} mappingCount={3}
      source={SQLSERVER} target={POSTGRES} sourceWords="erp (SQL Server)" targetWords="dw (PostgreSQL)" scriptNames={[]} />)
    expect(html).toContain('Gone, not offered by this driver')
    expect(html).toContain('All 3 table mappings')
    expect(html).toContain('The readers of the source, erp (SQL Server).')
  })

  it('counts the commits after the deployed build\'s commit, and none when it is not in the history', () => {
    const history = [{ sha: 'c3c3c3c', message: '', author: '', when: '' }, { sha: 'b2b2b2b', message: '', author: '', when: '' }, { sha: 'a1a1a1a', message: '', author: '', when: '' }]
    expect(commitsAfter(history, 'a1a1a1a1a1a1')).toBe(2)
    expect(commitsAfter(history, 'c3c3c3c')).toBe(0)
    expect(commitsAfter(history, 'ffff')).toBeNull()
    expect(commitsAfter(history, null)).toBeNull()
    expect(errorsUnder([{ severity: 'error', file: 'config\\replications\\a\\task.yaml' }, { severity: 'error', file: null }], 'config/replications/a/')).toBe(1)
  })

  it('marks the rail unpublished from the newest deploy, the commits after its build and the unsaved edits', () => {
    const version = { deploy: 'd1', build: 'b1', target: 'plant', targetName: 'plant', package: 'erp-sync', channel: 'dev', version: '7', mode: 'stage', tasks: [] }
    expect(publishStateOf(undefined, [])).toBeUndefined()
    expect(unpublishedOf(publishStateOf({ version: null, buildCommit: null, commitsSince: null }, ['config/replications/orders/task.yaml']), 'orders')).toBeNull()
    const state = publishStateOf({ version, buildCommit: 'a1', commitsSince: 0 }, ['config/replications/orders/task.yaml'])
    expect(state?.published).toEqual({ build: 'b1', version: '7', channel: 'dev', runner: 'plant', mode: 'stage' })
    expect(unpublishedOf(state, 'orders')).toEqual({ commits: 0, unsavedHere: 1, unsavedShared: 0 })
    expect(unpublishedOf(state, 'invoices')).toBeNull()
    expect(stagedLine(version, false)).toBe('Apply runs the task provision of erp-sync version 7, staged on dev of plant.')
    expect(stagedLine(null, true)).toBe('')
  })
})
