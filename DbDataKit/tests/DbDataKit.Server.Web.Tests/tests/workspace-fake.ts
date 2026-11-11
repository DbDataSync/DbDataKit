import type { Page, Route } from '@playwright/test'
import YAML from 'yaml'

/**
 * A Workspace server as the console's relay shows it, for the authoring story (plan 33 SC20f, SC21d, SC22d): the console's own `/api/instances` lists it and `/relay/<id>/api/v1/...` is answered here, in the page (the pattern of
 * `store-fake.ts`), so the story needs the signed-in console only and takes no port. The real routes are `DbDataWorkspace.Tests` and `DbDataKit.Authoring.Server`'s tests; the words of the check below are the ones the kinds'
 * validators say (`DataModelLoader`'s code, the Workspace's own refusal of a password, `dbdatabuild`'s DDB code), and the shapes are the ones the screens read. Files live in memory, a build always succeeds, and a publish
 * records what it was given, so the story can see that what the screens sent is what the server would have got.
 */
export const SERVER = 'ws-1'

type Kind = 'store' | 'sync' | 'build'
interface Project { id: string; kind: Kind; title: string | null; created: string }
interface Workspace { id: string; project: string; name: string; branch: string; base: string; created: string; files: Map<string, string>; builds: Build[] }
interface Build { id: string; kind: Kind; publishes: { at: string; by: string; target: string; ok: boolean; answer?: unknown }[] }
interface Finding { severity: 'error' | 'warning' | 'info'; code: string; message: string; file: string | null; line: number | null }

export interface FakeWorkspace {
  /** What each publish was sent: the project, the build and the body. */
  published: { project: string; body: Record<string, unknown> }[]
  files: (project: string, name: string) => Map<string, string> | undefined
}

const lineOf = (text: string, needle: string) => { const i = text.split('\n').findIndex((l) => l.includes(needle)); return i < 0 ? null : i + 1 }

/** What a kind's check says about a draft: the same words the validators use for the three faults the story makes. */
function check(kind: Kind, files: Map<string, string>): Finding[] {
  const found: Finding[] = []
  for (const [file, text] of files) {
    if (kind === 'store' && file.startsWith('models/') && /\.ya?ml$/.test(file)) {
      let root: any
      try { root = YAML.parse(text) } catch (e) { found.push({ severity: 'error', code: 'DDM-001', message: `${file} is not valid YAML.`, file, line: null }); continue }
      if (!root?.entities) found.push({ severity: 'error', code: 'DDM-010', message: 'A model has at least one entity.', file, line: null })
      if (text.includes('NOT_A_TYPE')) found.push({ severity: 'error', code: 'DDM-204', message: "The type 'NOT_A_TYPE' is not a type the models know.", file, line: lineOf(text, 'NOT_A_TYPE') })
    }
    if (kind === 'sync' && file.startsWith('config/connections/')) {
      text.split('\n').forEach((l, i) => {
        const m = /^\s*password_secret\s*:\s*(\S+)/.exec(l)
        if (m && !m[1].startsWith('secret:')) found.push({ severity: 'error', code: 'DBD-WS-SYNC-SECRET', message: `${file} has a \`password_secret\` that is not a secret reference.`, file, line: i + 1 })
      })
    }
    if (kind === 'build' && file.startsWith('models/') && file.endsWith('.sql') && text.includes('nowhere')) {
      found.push({ severity: 'error', code: 'DDB-301', message: "The table 'nowhere' is not defined by any model.", file, line: lineOf(text, 'nowhere') })
    }
  }
  return found
}

const template = (id: string, kind: Kind): Map<string, string> => {
  if (kind === 'store') return new Map([[`models/${id}.yaml`, ['schemaVersion: 2', `name: ${id}`, 'title: Sales', 'storage: {connection: mdm_sql}', 'entities:', '  item:', '    key: [code]', '    attributes:', '      code: {type: VARCHAR(20), required: true}', '      name: {type: VARCHAR(100), required: true}', ''].join('\n')]])
  if (kind === 'sync') return new Map([
    ['config/connections/source-db.yaml', 'schema: connection/1\nname: source-db\nengine: sqlserver\naddress:\n  host: source.example.com\n  database: App\nauth:\n  mode: sql\n  user: sync\n  password_secret: secret:dbdata:sync:connection:source-db\n'],
    ['config/connections/target-db.yaml', 'schema: connection/1\nname: target-db\nengine: postgres\naddress:\n  host: target.example.com\n  database: dw\nauth:\n  mode: sql\n  user: sync\n  password_secret: secret:dbdata:sync:connection:target-db\n'],
    ['config/replications/orders/task.yaml', 'schemaVersion: 1\nname: orders\nscheduling:\n  mode: Periodic\n  cronExpression: "*/15 * * * *"\nchangeProcessing:\n  reader: {kind: MsSqlChangeTracking, options: {}}\n  cache: {kind: MsSqlStagingTable, options: {}}\n  writer: {kind: PostgresMerge, options: {}}\n'],
    ['config/replications/orders/table-mappings/orders.yaml', 'schemaVersion: 1\nname: orders\nsources:\n- connectionName: source-db\n  schema: dbo\n  table: Orders\ntargets:\n- connectionName: target-db\n  schema: public\n  table: orders\n'],
  ])
  return new Map([
    ['dbdatabuild.yml', 'name: starter\n'],
    ['models/staging/orders.sql', 'SELECT 1 AS id\n'], ['models/staging/orders.yml', 'name: orders\n'],
    ['models/marts/fct_sales.sql', 'SELECT id FROM {{ ref(\'orders\') }}\n'],
    ['rendered/sqlserver/loads.yml', 'loads_file:\n  connection: "sqlserver"\nloads:\n- model: orders\n- model: fct_sales\ndependencies:\n- model: fct_sales\n  model_kind: full\n  depends_on: orders\n  depends_on_kind: full\n  depth: 1\nmodels:\n- name: orders\n  kind: full\n- name: fct_sales\n  kind: full\n'], ['rendered/sqlserver/refresh/hourly.yml', 'members:\n- model: orders\n- model: fct_sales\n'],
  ])
}

const KINDS = [
  { name: 'app', title: 'App', description: 'An app.', buildable: true, produces: false, publishes: [{ product: 'gateway', what: 'app', description: 'The built app.' }] },
  { name: 'build', title: 'Build project', description: 'Models of dbdatabuild.', buildable: false, produces: true, publishes: [{ product: 'runner', what: 'package', description: 'The refresh package.' }] },
  { name: 'store', title: 'Store model', description: 'Master data models.', buildable: false, produces: true, publishes: [{ product: 'gateway', what: 'model', description: 'The models.' }] },
  { name: 'sync', title: 'Sync replication set', description: 'Replications of dbdatasync.', buildable: false, produces: true, publishes: [{ product: 'runner', what: 'package', description: 'The replications.' }] },
]

function json(route: Route, body: unknown, status = 200) { return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) }) }

export async function mockWorkspace(page: Page): Promise<FakeWorkspace> {
  const projects: Project[] = []
  const workspaces: Workspace[] = []
  const published: FakeWorkspace['published'] = []
  let counter = 0
  const stamp = () => `2026-11-11T10:${String(counter++).padStart(2, '0')}:00Z`

  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Workspace server', url: 'https://workspace.invalid', product: 'dbdataworkspace', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())

  await page.route(`**/relay/${SERVER}/api/v1/**`, async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const tail = decodeURIComponent(url.pathname.slice(`/relay/${SERVER}/api/v1/`.length))
    const method = request.method()
    const body = request.postData() ?? ''
    const parts = tail.split('/')
    if (tail === 'project-kinds') return json(route, { kinds: KINDS })
    if (tail === 'projects' && method === 'GET') return json(route, { projects: projects.map((p) => ({ ...p, workspaces: workspaces.filter((w) => w.project === p.id).length })) })
    if (tail === 'projects' && method === 'POST') {
      const input = JSON.parse(body) as { id: string; title?: string; kind?: Kind }
      const project: Project = { id: input.id, kind: input.kind ?? ('app' as Kind), title: input.title ?? null, created: stamp() }
      projects.push(project)
      return json(route, { ...project, workspaces: 0 }, 201)
    }
    if (tail === 'publish-targets') {
      const kind = projects.find((p) => p.id === url.searchParams.get('project'))?.kind
      const gateway = kind === 'store'
      return json(route, { targets: [
        { name: 'edge', gateway: 'https://gateway.invalid', product: 'gateway', accepts: gateway, can_publish: true },
        { name: 'plant', gateway: 'https://runner.invalid', product: 'runner', accepts: !gateway, can_publish: true },
      ] })
    }
    if (parts[0] !== 'projects' || !parts[1]) return json(route, { error: 'There is no such call.' }, 404)
    const project = projects.find((p) => p.id === parts[1])
    if (!project) return json(route, { error: `There is no project '${parts[1]}'.` }, 404)
    if (parts[2] !== 'workspaces') return json(route, { error: 'There is no such call.' }, 404)
    if (parts.length === 3 && method === 'GET') return json(route, { workspaces: workspaces.filter((w) => w.project === project.id).map(({ files: _f, builds: _b, ...w }) => ({ ...w, owner: 'tester', mine: true })) })
    if (parts.length === 3 && method === 'POST') {
      const name = (JSON.parse(body) as { name: string }).name
      const w: Workspace = { id: `ws-${counter}-${name}`, project: project.id, name, branch: `workspace/${name}`, base: 'abc1234', created: stamp(), files: template(project.id, project.kind), builds: [] }
      workspaces.push(w)
      const { files: _f, builds: _b, ...summary } = w
      return json(route, { ...summary, owner: 'tester', mine: true }, 201)
    }
    const w = workspaces.find((x) => x.id === parts[3] && x.project === project.id)
    if (!w) return json(route, { error: 'There is no such workspace.' }, 404)
    const rest = parts.slice(4)
    if (rest[0] === 'files' && rest.length === 1) return json(route, { files: [...w.files].map(([path, text]) => ({ path, size: text.length })).sort((a, b) => a.path.localeCompare(b.path)), truncated: false, max_file_bytes: 1048576 })
    if (rest[0] === 'files') {
      const path = rest.slice(1).join('/')
      if (method === 'GET') return w.files.has(path) ? json(route, { path, content: w.files.get(path) }) : json(route, { error: `There is no file ${path}.` }, 404)
      if (method === 'PUT') { const created = !w.files.has(path); const content = (JSON.parse(body) as { content: string }).content; w.files.set(path, content); return json(route, { path, created, size: content.length }) }
      if (method === 'DELETE') { w.files.delete(path); return route.fulfill({ status: 204 }) }
    }
    if (rest[0] === 'check') return json(route, { kind: project.kind, ok: !check(project.kind, w.files).some((f) => f.severity === 'error'), diagnostics: check(project.kind, w.files) })
    if (rest[0] === 'changes') return json(route, { changes: [], uncommitted: true })
    if (rest[0] === 'history') return json(route, { commits: [] })
    if (rest[0] === 'events') return json(route, { events: [] })
    if (rest[0] === 'builds' && rest.length === 1 && method === 'GET') return json(route, { builds: w.builds.map((b) => buildSummary(w, b)) })
    if (rest[0] === 'builds' && rest.length === 1 && method === 'POST') {
      const b: Build = { id: `b${w.builds.length + 1}`, kind: project.kind, publishes: [] }
      w.builds.unshift(b)
      return json(route, buildSummary(w, b), 202)
    }
    if (rest[0] === 'builds' && rest.length === 2) {
      const b = w.builds.find((x) => x.id === rest[1])
      if (!b) return json(route, { error: 'There is no such build.' }, 404)
      const bad = check(project.kind, w.files).filter((f) => f.severity === 'error')
      const status = bad.length > 0 ? 'failed' : 'succeeded'
      return json(route, {
        build: { ...buildSummary(w, b), status },
        result: { status, message: status === 'failed' ? 'The draft has problems.' : 'Made.', diagnostics: bad, uncommitted_changes: true, commit: null, seconds: 1, kind: project.kind, info: status === 'succeeded' ? { package: project.id, version: '1' } : undefined },
        publishes: b.publishes,
      })
    }
    if (rest[0] === 'builds' && rest[2] === 'publish' && method === 'POST') {
      const b = w.builds.find((x) => x.id === rest[1])!
      const sent = JSON.parse(body) as Record<string, unknown>
      published.push({ project: project.id, body: sent })
      const answer = project.kind === 'store' ? { target: sent.target, gateway: 'https://gateway.invalid', models: [{ model: project.id, data_source: String(sent.data_source ?? 'mdm_sql'), version: 1, created: true }] } : { target: sent.target, runner: 'https://runner.invalid', package: project.id, channel: sent.channel, mode: sent.serve ? 'served' : 'staged', changed: true }
      b.publishes.push({ at: stamp(), by: 'tester', target: String(sent.target), ok: true, answer })
      return json(route, { steps: ['Read what was made.', 'Sent it.'], published: answer })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })

  return { published, files: (project, name) => workspaces.find((w) => w.project === project && w.name === name)?.files }
}

function buildSummary(w: Workspace, b: Build) {
  return { id: b.id, workspace: w.id, status: 'succeeded', commit: null, requested_by: 'tester', message: null, requested: '2026-11-11T10:30:00Z', started: null, finished: null }
}
