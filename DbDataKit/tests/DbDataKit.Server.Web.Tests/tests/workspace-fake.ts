import type { Page, Route } from '@playwright/test'
import YAML from 'yaml'

/**
 * A Workspace server as the console's relay shows it, for the authoring story (plan 33 SC20f, SC21d, SC22d): the console's own `/api/instances` lists it and `/relay/<id>/api/v1/...` is answered here, in the page (the pattern of
 * `store-fake.ts`), so the story needs the signed-in console only and takes no port. The real routes are `DbDataWorkspace.Tests` and `DbDataKit.Authoring.Server`'s tests; the words of the check below are the ones the kinds'
 * validators say (`DataModelLoader`'s code, the Workspace's own refusal of a password, `dbdatabuild`'s DDB code), and the shapes are the ones the screens read. Files live in memory, a build always succeeds, and a publish
 * records what it was given, so the story can see that what the screens sent is what the server would have got.
 *
 * A build is published by the console, not by the Workspace server: the console's `/api/instances` also lists a Runner `plant` and a Gateway `edge`, and its `/api/deploys` is answered here too (a deploy and the list of
 * deploys, in the shapes `DeploysController` answers; the real route is `ConsoleDeployTests`). A call to the Workspace server's own publish route is kept apart (`workspacePublishes`), so a story can say that none was made.
 * The runs of what a deploy put on the Runner go through the console too (`/api/deploys/{id}/runs`, `/runs/{run}`, `/binding-test`): a run is queued at first (the read answers 404, as a Runner that has not started it yet), then it has
 * succeeded with the steps its task records; a call to the Workspace server's own run routes is kept apart (`workspaceRuns`).
 */
export const SERVER = 'ws-1'
/** The Runner and the Gateway the console manages, by the id the console gives them (their names are the same). */
export const RUNNER = 'plant'
export const GATEWAY = 'edge'

type Kind = 'store' | 'sync' | 'build' | 'integration'
interface Project { id: string; kind: Kind; title: string | null; created: string }
interface Workspace { id: string; project: string; name: string; branch: string; base: string; created: string; files: Map<string, string>; builds: Build[] }
interface Build { id: string; kind: Kind; publishes: { at: string; by: string; target: string; ok: boolean; answer?: unknown }[] }
interface Finding { severity: 'error' | 'warning' | 'info'; code: string; message: string; file: string | null; line: number | null }

export interface FakeWorkspace {
  /** What each deploy asked the console for: the project and the body of `POST /api/deploys` (`target`, `channel`, `bind`, `adopt`, ...). */
  published: { project: string; body: Record<string, unknown> }[]
  /** The calls to the Workspace server's own publish route (`.../builds/{b}/publish`): the screens make none. */
  workspacePublishes: { project: string; body: Record<string, unknown> }[]
  files: (project: string, name: string) => Map<string, string> | undefined
  /** Set to a data source name and a Store publish is refused, as a Gateway does for a data source made by hand, until the body says `adopt` (plan 33 SC21f). */
  adoptRefusal: string | null
  /** What each run asked the console for: the deploy, the task, its inputs and whether the served version runs it. */
  runs: { deploy: string; task: string; inputs: Record<string, string>; served: boolean }[]
  /** The slots tested through the console, by deploy. */
  bindingTests: { deploy: string; slot: string }[]
  /** The activations asked of a version staged for its preflight, and whether the person confirmed what it found. */
  activations: { deploy: string; confirm: boolean }[]
  /** The calls to the Workspace server's own run routes (`.../builds/{b}/run`, `/runs/{run}`, `/binding-test`, `/contract`): the screens make none. */
  workspaceRuns: string[]
}

/** What a task records while it runs, as the real ones do: a replication's pass, or the statements provision ran. */
function stepsOf(task: string, inputs: Record<string, string>, step: string | null) {
  const at = '2026-11-11T11:00:00Z'
  if (step === 'pass-record') return [{ key: 'orders', started: at, completed: at, result: { mapping: 'orders', kind: 'Primary', status: 'Succeeded', rowsRead: 3, rowsWritten: 3 } }]
  // the preflight of a Build package: the status of its connection, with an error (the structure is behind the project) and a warning
  if (step === 'preflight-findings' && task === 'preflight') return [{ key: 'status warehouse', started: at, completed: at, result: { part: 'status warehouse', findings: PREFLIGHT_FINDINGS } }]
  if (step === 'provision-applied' && task === 'provision') return [{ key: inputs.mapping ?? '', started: at, completed: at, result: { steps: [{ title: `Create the table of ${inputs.mapping}`, succeeded: true }] } }]
  return []
}

/** What the preflight of a Build package finds in the stories: the deployed structure behind the project, and a table changed by hand. */
const PREFLIGHT_FINDINGS = [
  { severity: 'error', message: 'The deployed structure is behind the project: 2 deploy step(s) are pending (0 risky, 0 destructive), and a refresh is refused until they are applied (apply_plan).', about: 'warehouse' },
  { severity: 'warning', message: 'marts.dim_store was changed outside dbdatabuild since it was deployed.', about: 'warehouse' },
]

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
    if (kind === 'integration' && file.startsWith('src/') && file.endsWith('.csx') && text.includes('BROKEN')) {
      found.push({ severity: 'error', code: 'DBD-INT-CS1002', message: '; expected', file, line: lineOf(text, 'BROKEN') })
    }
    if (kind === 'build' && file.startsWith('models/') && file.endsWith('.sql') && text.includes('nowhere')) {
      found.push({ severity: 'error', code: 'DDB-301', message: "The table 'nowhere' is not defined by any model.", file, line: lineOf(text, 'nowhere') })
    }
  }
  return found
}

const template = (id: string, kind: Kind): Map<string, string> => {
  if (kind === 'store') return new Map([[`models/${id}.yaml`, ['schemaVersion: 2', `name: ${id}`, 'title: Sales', 'storage: {connection: mdm_sql}', 'entities:', '  item:', '    key: [code]', '    attributes:', '      code: {type: VARCHAR(20), required: true}', '      name: {type: VARCHAR(100), required: true}', ''].join('\n')]])
  if (kind === 'integration') return new Map([
    ['package.yaml', ['schema: package/1', `name: ${id}`, `launch: { kind: assembly, assembly: bin/${id}.dll }`, 'tasks:', '  - name: hello', '    entry: { type: Integration.Hello }', '    triggers:', '      - manual: true', ''].join('\n')],
    ['src/hello.csx', 'namespace Integration;\n\n[RunnerTask("hello")]\npublic sealed class Hello : RunnerJob<NoInput, NoProgress, NoSlots>\n{\n    protected override Task RunAsync(CancellationToken ct) => Task.CompletedTask;\n}\n'],
    ['tests/hello.csx', 'var sim = new RunSimulator<Integration.Hello>();\nawait sim.RunAsync();\nsim.Dispose();\n'],
    ['README.md', `# ${id}\n`],
  ])
  if (kind === 'sync') return new Map([
    ['config/connections/source-db.yaml', 'schema: connection/1\nname: source-db\nengine: sqlserver\naddress:\n  host: source.example.com\n  database: App\nauth:\n  mode: sql\n  user: sync\n  password_secret: secret:dbdatakit:sync:connection:source-db\n'],
    ['config/connections/target-db.yaml', 'schema: connection/1\nname: target-db\nengine: postgres\naddress:\n  host: target.example.com\n  database: dw\nauth:\n  mode: sql\n  user: sync\n  password_secret: secret:dbdatakit:sync:connection:target-db\n'],
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
  { name: 'build', title: 'Build project', description: 'Models of dbdatabuild.', buildable: false, produces: true, publishes: [{ product: 'runner', what: 'package', description: 'The refresh package.' }, { product: 'gateway', what: 'semantic-model', description: 'The compiled semantic models.' }] },
  { name: 'store', title: 'Store model', description: 'Master data models.', buildable: false, produces: true, publishes: [{ product: 'gateway', what: 'model', description: 'The models.' }] },
  { name: 'integration', title: 'Integration', description: 'Code for the Runner.', buildable: false, produces: true, publishes: [{ product: 'runner', what: 'package', description: 'The integration as a package.' }] },
  { name: 'sync', title: 'Sync replication set', description: 'Replications of dbdatasync.', buildable: false, produces: true, publishes: [{ product: 'runner', what: 'package', description: 'The replications.' }] },
]

function json(route: Route, body: unknown, status = 200) { return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) }) }

export async function mockWorkspace(page: Page): Promise<FakeWorkspace> {
  const projects: Project[] = []
  const workspaces: Workspace[] = []
  const published: FakeWorkspace['published'] = []
  const workspacePublishes: FakeWorkspace['workspacePublishes'] = []
  const deploys: Record<string, unknown>[] = []
  const runs: FakeWorkspace['runs'] = []
  const bindingTests: FakeWorkspace['bindingTests'] = []
  const activations: FakeWorkspace['activations'] = []
  const workspaceRuns: string[] = []
  const reads = new Map<string, number>()
  const fake = { adoptRefusal: null as string | null }
  let counter = 0
  const stamp = () => `2026-11-11T10:${String(counter++).padStart(2, '0')}:00Z`
  const server = (id: string, name: string, product: string, url: string) => ({ id, name, url, product, version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null })

  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [server(SERVER, 'Workspace server', 'dbdataworkspace', 'https://workspace.invalid'), server(RUNNER, RUNNER, 'dbdatarunner', 'https://runner.invalid'), server(GATEWAY, GATEWAY, 'dbdatagateway', 'https://gateway.invalid')])
    : route.fallback())

  // the console's deploy of a build: what the target answers by its product, a refusal for a data source made by hand until `adopt` names it, and the record of each deploy, taken or refused
  await page.route((url) => url.pathname === '/api/deploys', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    if (request.method() === 'GET') {
      const narrow = ['workspace_server', 'project', 'workspace', 'build'] as const
      return json(route, { deploys: deploys.filter((d) => narrow.every((k) => !url.searchParams.get(k) || d[k] === url.searchParams.get(k))) })
    }
    const sent = JSON.parse(request.postData() ?? '{}') as Record<string, unknown>
    const project = projects.find((p) => p.id === sent.project)
    if (!project) return json(route, { error: `There is no project '${String(sent.project)}'.`, steps: [] }, 404)
    published.push({ project: project.id, body: sent })
    const target = String(sent.target)
    const gateway = target === GATEWAY
    const record = (outcome: string, answer: Record<string, unknown> | null, error: string | null) => {
      const kept: Record<string, unknown> = { id: `deploy-${counter}`, workspace_server: sent.workspaceServer, project: project.id, workspace: sent.workspace, build: sent.build, item: gateway ? 'model' : 'package', kind: project.kind,
        target, target_name: target, product: gateway ? 'gateway' : 'runner', route: gateway ? 'model' : 'package', name: project.id, channel: gateway ? null : sent.channel ?? null,
        version: answer ? 1 : null, mode: gateway || !answer ? null : sent.serve ? 'serve' : 'stage', signature: answer ? 'verified' : null, deployed_by: 'tester', deployed_at: stamp(), outcome, error,
        tasks: answer && !gateway ? (answer.tasks as string[] | undefined) ?? [] : [], preflight: answer?.preflight ?? null }
      if (answer?.mode === 'stage') kept.mode = 'stage'
      deploys.unshift(kept)
      return kept
    }
    const steps = [`Read build ${String(sent.build)} of ${project.id}/${String(sent.workspace)} from Workspace server.`, 'Read the files of the build and checked each against its SHA-256.']
    if (project.kind === 'store' && fake.adoptRefusal && !(Array.isArray(sent.adopt) && sent.adopt.includes(fake.adoptRefusal))) {
      const error = `The Gateway refused the model ${project.id} on the data source '${fake.adoptRefusal}' (400): it was made by hand. Publish again with "adopt".`
      return json(route, { error, steps, adopt: { data_source: fake.adoptRefusal, made_by: 'hand' }, deploy: record('refused', null, error) }, 409)
    }
    const answer = gateway
      ? { target, gateway: 'https://gateway.invalid', models: [{ model: project.id, data_source: String(sent.dataSource ?? 'mdm_sql'), version: 1, created: true, adopted: Array.isArray(sent.adopt) ? sent.adopt[0] : null }] }
      : { target, runner: 'https://runner.invalid', package: project.id, channel: sent.channel, mode: sent.serve ? 'serve' : 'stage', version: 1, changed: true, signature: 'verified',
          tasks: project.kind === 'sync' ? ['orders', 'provision'] : project.kind === 'build' ? [`${project.id}-nightly`, 'preflight'] : [] } as Record<string, unknown>
    // a Build package served in prod waits for its preflight, which finds an error: the version stays staged and the deploy answers 409 with what was found
    if (!gateway && project.kind === 'build' && sent.channel === 'prod' && sent.serve) {
      answer.mode = 'stage'
      answer.preflight = { declared: true, run: `6f1c0f1e-0000-4000-8000-${String(runs.length + 1).padStart(12, '0')}`, link: `/servers/${target}/runs?run=6f1c0f1e-0000-4000-8000-${String(runs.length + 1).padStart(12, '0')}`,
        state: 'finished', outcome: 'succeeded', errors: 1, warnings: 1, findings: PREFLIGHT_FINDINGS, activation: 'refused' }
      runs.push({ deploy: `deploy-${counter}`, task: 'preflight', inputs: {}, served: false })
      const error = `The preflight of version 1 found 1 error, so it was not activated in prod: warehouse: ${PREFLIGHT_FINDINGS[0].message} It stays staged. To activate it anyway, confirm; the confirmation is kept in the Runner's record of the activation.`
      return json(route, { error, steps: [...steps, `Staged ${project.id} on prod: version 1.`, 'The preflight found 1 error(s) and 1 warning(s).'], deployed: answer, deploy: record('ok', answer, null) }, 409)
    }
    return json(route, { steps, deployed: answer, deploy: record('ok', answer, null) })
  })

  // the runs of what a deploy put on the Runner, through the console as the person
  await page.route((url) => url.pathname.startsWith('/api/deploys/'), async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const [deployId, what, runId] = url.pathname.slice('/api/deploys/'.length).split('/').map(decodeURIComponent)
    const deploy = deploys.find((d) => d.id === deployId)
    if (!deploy) return json(route, { error: `The console has no deploy '${deployId}'.` }, 404)
    if (what === 'runs' && request.method() === 'POST') {
      const body = JSON.parse(request.postData() ?? '{}') as { task: string; inputs?: Record<string, string>; served?: boolean }
      runs.push({ deploy: deployId!, task: body.task, inputs: body.inputs ?? {}, served: body.served === true })
      const id = `6f1c0f1e-0000-4000-8000-${String(runs.length).padStart(12, '0')}`
      return json(route, { steps: [`Started ${body.task} of ${String(deploy.name)}: run ${id}.`], run: { run_id: id, task: body.task, package: deploy.name, channel: deploy.channel, version: body.served ? 'served' : String(deploy.version), target: deploy.target_name, runner: 'https://runner.invalid', instance: deploy.target, on_behalf_of: 'tester' } }, 202)
    }
    if (what === 'runs' && runId && request.method() === 'GET') {
      const index = Number(runId.slice(-12)) - 1
      const started = runs[index]
      if (!started) return json(route, { error: `${String(deploy.target_name)} has no run ${runId}.` }, 404)
      const n = (reads.get(runId) ?? 0) + 1
      reads.set(runId, n)
      if (n === 1) return json(route, { error: `${String(deploy.target_name)} has no run ${runId}.` }, 404)   // queued: not on the Runner's record yet
      return json(route, { run: { id: runId, task: started.task, outcome: 'succeeded', started: '2026-11-11T11:00:00Z', finished: '2026-11-11T11:00:05Z', version: String(deploy.version), failure: null, steps: stepsOf(started.task, started.inputs, url.searchParams.get('step')) } })
    }
    // the activation of a version staged for its preflight: refused while its error is not confirmed, the confirmation kept with it
    if (what === 'activate' && request.method() === 'POST') {
      const body = JSON.parse(request.postData() ?? '{}') as { confirm?: boolean }
      const preflight = deploy.preflight as Record<string, unknown> | null
      if (!preflight) return json(route, { error: 'This deploy did not stage a version for a preflight.' }, 409)
      activations.push({ deploy: deployId!, confirm: body.confirm === true })
      if (!body.confirm) return json(route, { error: 'The preflight of version 1 found 1 error, so it was not activated in prod.' }, 409)
      deploy.mode = 'serve'
      deploy.preflight = { ...preflight, activation: 'activated', confirmed_by: 'tester' }
      return json(route, { activation: 'activated', message: `Version 1 of ${String(deploy.name)} is active in prod.`, active: 1, preflight: deploy.preflight, deploy })
    }
    if (what === 'binding-test' && request.method() === 'POST') {
      const body = JSON.parse(request.postData() ?? '{}') as { slot: string }
      bindingTests.push({ deploy: deployId!, slot: body.slot })
      return json(route, { test: { slot: body.slot, ok: false, step: 'connection', message: `${String(deploy.target_name)} has no connection erp.`, connected: false, package: deploy.name, channel: deploy.channel, target: deploy.target_name, instance: deploy.target } })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })

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
    // the plan of a replication's tables, made offline from the draft's catalog cache: the target table is not there yet
    if (rest[0] === 'provisioning-plan' && method === 'POST') {
      const replication = (JSON.parse(body || '{}') as { replication?: string }).replication ?? ''
      return json(route, { ok: true, diagnostics: [], data: { replication, cache_generated_at: '2026-11-11T09:00:00Z', cache_server: 'edge', mappings: [{
        mapping: 'orders', source: { connection: 'source-db', table: 'dbo.Orders' }, target: { connection: 'target-db', table: 'public.orders' }, excluded: null,
        source_plan: { action: 'enableSourceChangeCapture', state: 'satisfied', steps: [], warnings: [] },
        target_plan: { action: 'createTargetTable', state: 'missing', steps: [{ title: 'Create the table', command_text: 'CREATE TABLE "public"."orders" ("id" bigint)', rationale: 'it is not there', scope: 'table' }], warnings: [] },
      }] } })
    }
    // the Workspace server's own run routes: the screens run through the console
    if (rest[0] === 'builds' && (rest[2] === 'run' || rest[2] === 'runs' || rest[2] === 'binding-test' || rest[2] === 'contract')) {
      workspaceRuns.push(`${method} ${tail}`)
      return json(route, { error: 'This build has not been staged on a Runner.' }, 409)
    }
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
    // the Workspace server's own publish, with its publisher key: no screen calls it any more (the Builds tab and the Store project's Deploy tab deploy through the console)
    if (rest[0] === 'builds' && rest[2] === 'publish' && method === 'POST') {
      const b = w.builds.find((x) => x.id === rest[1])!
      const sent = JSON.parse(body) as Record<string, unknown>
      workspacePublishes.push({ project: project.id, body: sent })
      if (project.kind === 'store' && fake.adoptRefusal && !(Array.isArray(sent.adopt) && sent.adopt.includes(fake.adoptRefusal)))
        return json(route, { error: `The Gateway refused the model ${project.id} on the data source '${fake.adoptRefusal}' (400): it was made by hand. Publish again with "adopt".`, steps: ['Asked the Gateway for the version it serves.'], adopt: { data_source: fake.adoptRefusal, made_by: 'hand' } }, 409)
      const answer = project.kind === 'store' ? { target: sent.target, gateway: 'https://gateway.invalid', models: [{ model: project.id, data_source: String(sent.data_source ?? 'mdm_sql'), version: 1, created: true, adopted: Array.isArray(sent.adopt) ? sent.adopt[0] : null }] } : { target: sent.target, runner: 'https://runner.invalid', package: project.id, channel: sent.channel, mode: sent.serve ? 'served' : 'staged', changed: true }
      b.publishes.push({ at: stamp(), by: 'tester', target: String(sent.target), ok: true, answer })
      return json(route, { steps: ['Read what was made.', 'Sent it.'], published: answer })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })

  return { published, workspacePublishes, runs, bindingTests, activations, workspaceRuns, files: (project, name) => workspaces.find((w) => w.project === project && w.name === name)?.files, get adoptRefusal() { return fake.adoptRefusal }, set adoptRefusal(v) { fake.adoptRefusal = v } }
}

function buildSummary(w: Workspace, b: Build) {
  return { id: b.id, workspace: w.id, status: 'succeeded', commit: null, requested_by: 'tester', message: null, requested: '2026-11-11T10:30:00Z', started: null, finished: null }
}
