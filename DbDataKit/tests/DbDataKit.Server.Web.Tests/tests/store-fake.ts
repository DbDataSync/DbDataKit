import type { Page, Route } from '@playwright/test'
import { createHash } from 'node:crypto'
import YAML, { LineCounter } from 'yaml'

/**
 * A DbDataStore server as the console's relay shows it, for the Store stories (plan 33, UE13, UE14, UE18): the console's own `/api/instances` lists it and `/relay/<id>/api/v1/...` is answered here, in the page, so the story needs
 * the signed-in console only (the pattern of `views-published.ts`). The real server's side is `DbDataStore.Tests` (`StoreModelsTests`, `StoreDataTests`, `StoreChangeRequestTests`) on the real routes; the words below (the loader's
 * diagnostics, the writer's violations, the routes' refusals) are copied from what the real server answers, and the shapes are the ones the screens read. Storage here is a set of table names, and the rules the writer checks are
 * the ones the stories break: required, min, max, allowed, a key that exists already.
 */
export const SERVER = 'store-1'

export type FakeRole = 'admin' | 'author' | 'viewer'

interface Row { values: Record<string, unknown>; version: number; valid_from: string | null; valid_to: string | null }
interface Request { id: string; model: string; instance: string; state: string; proposed_by: string; proposed_by_name: string | null; by_agent: boolean; proposed_at: string; description: string | null; changes: Change[]; decided_by: string | null; decided_at: string | null; note: string | null }
interface Change { action: string; entity: string; key?: Record<string, unknown>; expected_version?: number; values?: Record<string, unknown> }
interface Copy { created: boolean; definition: string | null; tables: Set<string>; columns: Map<string, Set<string>>; rows: Map<string, Row[]>; history: Map<string, Row[]> }

export interface FakeOptions {
  /** What the signed-in person may do on the server: admin has every role, author may write and approve a steward's requests, viewer reads. */
  role?: FakeRole
  /** Model texts the server holds, by name, with their tables not made yet. */
  models?: Record<string, string>
  /** Models whose tables exist (made at the model's text as it is); `model/instance` for an instance's copy. */
  created?: string[]
  /** The instances each model has, by name. */
  instances?: Record<string, string[]>
}

export interface FakeStore {
  /** Every call made, as `METHOD path`. */
  calls: string[]
  /** What a model's text is now. */
  text(name: string): string | undefined
  /** The open rows of an entity of a model's copy. */
  rows(model: string, entity: string, instance?: string): Record<string, unknown>[]
  /** Somebody else changed a row (its version moves), so a save of the old one is a conflict. */
  touch(model: string, entity: string, key: Record<string, unknown>, values: Record<string, unknown>): void
  /** A request an agent proposed, waiting. */
  propose(model: string, changes: Change[], description: string): string
  /** Somebody else saved the model (its version moves). */
  edit(name: string, text: string): void
  setRole(role: FakeRole): void
}

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const sha = (text: string) => createHash('sha256').update(text).digest('hex')
const MODEL_NAME = /^[a-z0-9][a-z0-9_-]{0,63}$/

interface Diagnostic { code: string; severity: string; file: string; line: number; column: number; message: string; fix: string | null }

const TYPE = /^(VARCHAR\(\d+\)|CHAR\(\d+\)|INTEGER|INT|BIGINT|SMALLINT|BOOLEAN|DATE|TIMESTAMP|TEXT|DECIMAL\(\d+,\s*\d+\)|NUMERIC\(\d+,\s*\d+\)|DOUBLE|FLOAT)$/i

/** What the loader says of a text, in its words (the file is `<name>.yaml`, positions are the text's). */
function check(name: string, text: string): { diagnostics: Diagnostic[]; model: ParsedModel | null } {
  const file = `${name}.yaml`
  const lines = new LineCounter()
  const doc = YAML.parseDocument(text, { lineCounter: lines })
  const diagnostics: Diagnostic[] = doc.errors.map((e) => ({ code: 'DBD-100', severity: 'error', file, line: e.linePos?.[0].line ?? 1, column: e.linePos?.[0].col ?? 1, message: e.message.split('\n')[0]!, fix: null }))
  if (diagnostics.length > 0) return { diagnostics, model: null }
  const at = (path: (string | number)[]) => {
    const node = doc.getIn(path, true) as { range?: [number, number, number] } | undefined
    const pos = node?.range ? lines.linePos(node.range[0]) : { line: 1, col: 1 }
    return { line: pos.line, column: pos.col }
  }
  const root = (doc.toJS() ?? {}) as Record<string, any>
  const add = (code: string, path: (string | number)[], message: string, fix: string | null = null) => diagnostics.push({ code, severity: 'error', file, ...at(path), message, fix })
  if (root.schemaVersion !== 1 && root.schemaVersion !== 2) add('DBD-101', ['schemaVersion'], 'The file needs `schemaVersion: 1`.')
  if (typeof root.name !== 'string' || root.name === '') add('DBD-102', ['name'], 'The model needs a `name`.')
  else if (root.name !== name) add('DBD-MDL-001', ['name'], `The model is called '${root.name}' but its file is ${file}; a model's file is named for it.`, `Rename the file or set name: ${name}.`)
  const entities = (root.entities ?? {}) as Record<string, any>
  if (Object.keys(entities).length === 0) add('DBD-103', ['entities'], 'A model needs at least one entity under `entities`.')
  const parsed: ParsedModel = { name: root.name ?? name, title: root.title ?? null, approval: root.approval?.changes ?? 'none', instances: root.storage?.instances ?? 'none', entities: [] }
  for (const [entityName, entity] of Object.entries(entities)) {
    const attributes = (entity?.attributes ?? {}) as Record<string, any>
    const known = new Set(Object.keys(attributes))
    for (const k of entity?.key ?? []) if (!known.has(String(k))) add('DBD-MDL-003', ['entities', entityName, 'key'], `\`${k}\` is in the key of \`${entityName}\` but is not one of its attributes. (Attributes: ${[...known].join(', ')}.)`)
    const attrs: ParsedAttribute[] = []
    for (const [attributeName, attribute] of Object.entries(attributes)) {
      const type = String(attribute?.type ?? '')
      if (!TYPE.test(type)) add('DBD-MDL-002', ['entities', entityName, 'attributes', attributeName, 'type'], `\`${type}\` is not a type the models know. Use a name like VARCHAR(40), INTEGER, DECIMAL(10,2), BOOLEAN, DATE or TIMESTAMP.`)
      for (const k of Object.keys(attribute ?? {})) {
        if (!['title', 'type', 'required', 'unique', 'default', 'pattern', 'min', 'max', 'allowed', 'references'].includes(k)) add('DBD-104', ['entities', entityName, 'attributes', attributeName, k], `Unknown key \`${k}\` in the attribute \`${entityName}.${attributeName}\`. (Keys: title, type, required, unique, default, pattern, min, max, allowed, references.)`)
      }
      attrs.push({ name: attributeName, type, required: attribute?.required === true, unique: attribute?.unique === true, min: attribute?.min ?? null, max: attribute?.max ?? null, allowed: attribute?.allowed ?? null })
    }
    parsed.entities.push({ name: entityName, key: (entity?.key ?? []).map(String), history: entity?.history ?? 'none', attributes: attrs })
  }
  return { diagnostics, model: diagnostics.length === 0 ? parsed : null }
}

interface ParsedAttribute { name: string; type: string; required: boolean; unique: boolean; min: number | null; max: number | null; allowed: string[] | null }
interface ParsedEntity { name: string; key: string[]; history: string; attributes: ParsedAttribute[] }
interface ParsedModel { name: string; title: string | null; approval: string; instances: string; entities: ParsedEntity[] }

const kindOf = (type: string) => /^(VARCHAR|CHAR|TEXT)/i.test(type) ? 'string' : /^(DECIMAL|NUMERIC)/i.test(type) ? 'decimal' : /^BOOLEAN/i.test(type) ? 'boolean' : /^(DOUBLE|FLOAT)/i.test(type) ? 'double' : /^(DATE)$/i.test(type) ? 'date' : /^TIMESTAMP/i.test(type) ? 'timestamp' : 'int32'
const lengthOf = (type: string) => /^(VARCHAR|CHAR)\((\d+)\)/i.exec(type)?.[2] ?? null
const definitionOf = (model: ParsedModel) => sha(JSON.stringify(model.entities.map((e) => [e.name, e.key, e.history, e.attributes.map((a) => [a.name, a.type])])))

/** The Store's answers for a story, over a page. The first role is what the signed-in person holds on this server. */
export async function mockStore(page: Page, options: FakeOptions = {}): Promise<FakeStore> {
  let role: FakeRole = options.role ?? 'admin'
  const texts = new Map<string, string>(Object.entries(options.models ?? {}))
  const copies = new Map<string, Copy>()
  const requests: Request[] = []
  const instances = new Map<string, string[]>(Object.entries(options.instances ?? {}))
  const calls: string[] = []
  let clock = Date.parse('2026-11-08T10:00:00Z')
  const now = () => new Date((clock += 60_000)).toISOString()

  const copyOf = (model: string, instance: string) => {
    const key = `${model}/${instance}`
    let c = copies.get(key)
    if (!c) { c = { created: false, definition: null, tables: new Set(), columns: new Map(), rows: new Map(), history: new Map() }; copies.set(key, c) }
    return c
  }
  const makeTables = (model: ParsedModel, c: Copy) => {
    for (const e of model.entities) { c.tables.add(e.name); c.columns.set(e.name, new Set(e.attributes.map((a) => a.name))); if (!c.rows.has(e.name)) c.rows.set(e.name, []) }
    c.created = true
    c.definition = definitionOf(model)
  }
  for (const made of options.created ?? []) { const [name, instance = 'default'] = made.split('/') as [string, string?]; const m = check(name, texts.get(name)!).model!; makeTables(m, copyOf(name, instance ?? 'default')) }

  const can = {
    write: () => role !== 'viewer',
    admin: () => role === 'admin',
  }
  const need = (route: Route, ok: boolean, what: string) => { if (!ok) { void json(route, { error: `This needs the ${what} role.` }, 403); return false } return true }

  const modelOf = (route: Route, name: string) => {
    const text = texts.get(name)
    if (text === undefined) { void json(route, { error: `There is no model '${name}'.` }, 404); return null }
    const { model } = check(name, text)
    if (!model) { void json(route, { error: `The model '${name}' is not valid.` }, 422); return null }
    return model
  }

  const statements = (model: ParsedModel, schema: string, only?: Set<string>) => model.entities.filter((e) => !only || only.has(e.name)).map((e) => ({
    object: `${schema}.${e.name}`, description: `Create the table ${e.name}`,
    text: `CREATE TABLE ${schema}.${e.name} (${e.attributes.map((a) => `${a.name} ${a.type}${a.required || e.key.includes(a.name) ? ' NOT NULL' : ''}`).join(', ')}, PRIMARY KEY (${e.key.join(', ')}))`,
  }))

  const violationsOf = (entity: ParsedEntity, values: Record<string, unknown>, key: Record<string, unknown>, creating: boolean, existing: Row[]) => {
    const found: { rule: string; entity: string; attribute: string | null; key: Record<string, unknown>; message: string }[] = []
    const add = (rule: string, attribute: string | null, message: string) => found.push({ rule, entity: entity.name, attribute, key, message })
    for (const a of entity.attributes) {
      const raw = values[a.name]
      const empty = raw === undefined || raw === null || raw === ''
      if (empty) { if (a.required || entity.key.includes(a.name)) add('required', a.name, `\`${entity.name}.${a.name}\` is required.`); continue }
      const n = typeof raw === 'number' ? raw : Number(raw)
      if (kindOf(a.type) === 'decimal' || kindOf(a.type) === 'int32') {
        if (a.min !== null && n < a.min) add('min', a.name, `\`${entity.name}.${a.name}\` is ${n}, below the minimum ${a.min}.`)
        if (a.max !== null && n > a.max) add('max', a.name, `\`${entity.name}.${a.name}\` is ${n}, above the maximum ${a.max}.`)
      }
      if (a.allowed && !a.allowed.includes(String(raw))) add('allowed', a.name, `\`${entity.name}.${a.name}\` is \`${String(raw)}\`, which is not one of: ${a.allowed.join(', ')}.`)
    }
    if (creating && found.length === 0 && existing.some((r) => entity.key.every((k) => String(r.values[k]) === String(values[k])))) add('key', null, `\`${entity.name}\` already has a row ${entity.key.map((k) => `${k}=${String(values[k])}`).join(', ')}.`)
    return found
  }

  const tick = (c: Copy, entity: ParsedEntity, row: Row, closed: boolean) => {
    if (entity.history === 'none') return
    const list = c.history.get(entity.name) ?? []
    c.history.set(entity.name, list)
    list.push({ ...row, values: { ...row.values }, valid_from: now(), valid_to: closed ? now() : null })
  }

  /** Applies a batch the way the writer does: every rule, every version, nothing written when anything is refused. */
  const apply = (model: ParsedModel, c: Copy, changes: Change[]): { status: number; body: unknown } | null => {
    const staged = new Map<string, Row[]>([...c.rows].map(([k, v]) => [k, v.map((r) => ({ ...r, values: { ...r.values } }))]))
    const violations: ReturnType<typeof violationsOf> = []
    const logged: { entity: ParsedEntity; row: Row; closed: boolean }[] = []
    for (const change of changes) {
      const entity = model.entities.find((e) => e.name === change.entity)
      if (!entity) return { status: 422, body: { error: `The model has no entity '${change.entity}'.` } }
      const list = staged.get(entity.name) ?? []
      staged.set(entity.name, list)
      const same = (r: Row, key: Record<string, unknown>) => entity.key.every((k) => String(r.values[k]) === String(key[k]))
      if (change.action === 'insert') {
        const v = violationsOf(entity, change.values ?? {}, Object.fromEntries(entity.key.map((k) => [k, change.values?.[k]])), true, list)
        violations.push(...v)
        if (v.length === 0) { const row: Row = { values: normalise(entity, change.values ?? {}), version: 1, valid_from: null, valid_to: null }; list.push(row); logged.push({ entity, row, closed: false }) }
      } else {
        const row = list.find((r) => same(r, change.key ?? {}))
        if (!row) return { status: 404, body: { error: `\`${entity.name}\` has no row ${entity.key.map((k) => `${k}=${String(change.key?.[k])}`).join(', ')}.` } }
        if (change.expected_version !== undefined && change.expected_version !== row.version) return { status: 409, body: { error: `\`${entity.name}\` ${entity.key.map((k) => String(row.values[k])).join('/')} was changed by someone else (it is at version ${row.version}, you read ${change.expected_version}).`, current_version: row.version } }
        if (change.action === 'delete') { list.splice(list.indexOf(row), 1); logged.push({ entity, row: { ...row, version: row.version + 1 }, closed: true }) }
        else {
          const merged = { ...row.values, ...(change.values ?? {}) }
          const v = violationsOf(entity, merged, change.key ?? {}, false, list)
          violations.push(...v)
          if (v.length === 0) { row.values = normalise(entity, merged); row.version += 1; logged.push({ entity, row: { ...row, values: { ...row.values } }, closed: false }) }
        }
      }
    }
    if (violations.length > 0) return { status: 422, body: { error: "The change breaks the model's rules, so nothing was written.", violations } }
    c.rows = staged
    for (const l of logged) tick(c, l.entity, l.row, l.closed)
    return null
  }
  const normalise = (entity: ParsedEntity, values: Record<string, unknown>) => Object.fromEntries(entity.attributes.map((a) => {
    const raw = values[a.name]
    if (raw === undefined || raw === '') return [a.name, null]
    return [a.name, kindOf(a.type) === 'decimal' || kindOf(a.type) === 'int32' ? Number(raw) : raw]
  }))

  const requestJson = (r: Request, withChanges: boolean) => ({
    id: r.id, model: r.model, instance: r.instance, state: r.state, proposed_by: r.proposed_by, proposed_by_name: r.proposed_by_name, by_agent: r.by_agent, proposed_at: r.proposed_at, description: r.description,
    change_count: r.changes.length, decided_by: r.decided_by, decided_at: r.decided_at, note: r.note, ...(withChanges ? { changes: r.changes } : {}),
  })
  const approver = (model: ParsedModel) => (model.approval === 'owner' ? 'admin' : 'author')
  const mayDecide = (model: ParsedModel) => (approver(model) === 'admin' ? role === 'admin' : role !== 'viewer')

  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Master data store', url: 'https://store.invalid', product: 'dbdatastore', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())

  await page.route(`**/relay/${SERVER}/api/v1/**`, async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const tail = decodeURIComponent(url.pathname.slice(`/relay/${SERVER}/api/v1/`.length))
    const method = request.method()
    calls.push(`${method} ${tail}${url.search}`)
    const body = request.postData() ?? ''
    const parts = tail.split('/')
    if (tail === 'models' && method === 'GET') {
      return json(route, { models: [...texts.keys()].sort().map((name) => { const { model } = check(name, texts.get(name)!); return { name, title: model?.title ?? null, valid: model !== null, entities: model?.entities.map((e) => e.name) ?? [] } }) })
    }
    if (parts[0] !== 'models' || !parts[1]) return json(route, { error: 'There is no such call.' }, 404)
    const name = parts[1]
    if (parts.length === 2) {
      if (method === 'GET') {
        const text = texts.get(name)
        if (text === undefined) return json(route, { error: `There is no model '${name}'.` }, 404)
        return json(route, { name, text, version: sha(text), diagnostics: check(name, text).diagnostics, can: { save: can.write(), remove: can.admin(), admin: can.admin() } })
      }
      if (method === 'PUT') {
        if (!need(route, can.write(), 'author')) return
        if (!MODEL_NAME.test(name)) return json(route, { error: `'${name}' is not a model name: lower-case letters, digits, '-' and '_', starting with a letter or digit, at most 64.` }, 400)
        const expected = url.searchParams.get('expected_version')
        const held = texts.has(name) ? sha(texts.get(name)!) : null
        if (expected && held !== expected) return json(route, { error: held === null ? `The model '${name}' was removed since you read it.` : `The model '${name}' was changed by someone else since you read it.`, current_version: held }, 409)
        const text = body.replace(/\r\n/g, '\n')
        const { diagnostics } = check(name, text)
        if (diagnostics.length > 0) return json(route, { error: `${name}.yaml is not a valid model, so nothing was saved.`, diagnostics }, 422)
        const existed = texts.has(name)
        texts.set(name, text)
        return json(route, { name, created: !existed, version: sha(text), diagnostics }, existed ? 200 : 201)
      }
      if (method === 'DELETE') {
        if (!need(route, can.admin(), 'admin')) return
        if (!texts.delete(name)) return json(route, { error: `There is no model '${name}'.` }, 404)
        return route.fulfill({ status: 204 })
      }
    }
    if (parts[2] === 'validate' && method === 'POST') {
      const { diagnostics } = check(name, body)
      return json(route, { valid: diagnostics.length === 0, diagnostics })
    }
    const model = modelOf(route, name)
    if (!model) return
    if (parts[2] === 'instances' && parts.length === 3) {
      return json(route, { mode: model.instances, instances: (instances.get(name) ?? []).map((i) => ({ name: i, connection: null, schema: `${name}__${i}` })) })
    }
    if (parts[2] === 'migrations') {
      const current = definitionOf(model)
      const names = ['default', ...(model.instances === 'column' ? [] : instances.get(name) ?? [])]
      return json(route, { definition: current, instances: names.map((n) => { const c = copyOf(name, n); return { name: n, definition: c.definition, applied_at: c.definition ? '2026-11-08T10:00:00Z' : null, status: c.definition === null ? 'unknown' : c.definition === current ? 'current' : 'behind' } }) })
    }
    // the routes under the model's own copy, or under an instance
    let rest = parts.slice(2)
    let instance = 'default'
    if (rest[0] === 'instances' && rest[1]) {
      instance = rest[1]
      if (method === 'PUT' && rest.length === 2) { if (!need(route, can.admin(), 'admin')) return; instances.set(name, [...new Set([...(instances.get(name) ?? []), instance])]); return json(route, { name: instance }, 201) }
      if (!(instances.get(name) ?? []).includes(instance)) return json(route, { error: `The model '${name}' has no instance '${instance}'.` }, 404)
      rest = rest.slice(2)
    }
    const c = copyOf(name, instance)
    const schema = instance === 'default' ? name : `${name}__${instance}`
    const op = rest.join('/')
    if (op === 'schema') {
      return json(route, {
        name, title: model.title, approval: model.approval, instances: model.instances,
        entities: model.entities.map((e) => ({
          name: e.name, title: null, key: e.key, history: e.history === 'effective_dated' ? 'effectivedated' : e.history, role: 'table', rules: [],
          attributes: e.attributes.map((a) => ({ name: a.name, title: null, type: a.type, kind: kindOf(a.type), length: lengthOf(a.type) ? Number(lengthOf(a.type)) : null, precision: null, scale: null, required: a.required, unique: a.unique, default: null, pattern: null, min: a.min, max: a.max, allowed: a.allowed, references: null })),
        })),
      })
    }
    if (op === 'storage' && method === 'GET') return json(route, { engine: 'postgres', statements: statements(model, schema), existing: c.created ? [...c.tables] : [] })
    if (op === 'storage/create' && method === 'POST') {
      if (!need(route, can.admin(), 'admin')) return
      if (c.created) return json(route, { error: `The tables of '${name}' are already there: ${[...c.tables].join(', ')}.` }, 422)
      const made = statements(model, schema)
      makeTables(model, c)
      return json(route, { engine: 'postgres', statements: made }, 201)
    }
    if (op === 'migrate' && method === 'POST') {
      if (!need(route, can.admin(), 'admin')) return
      const applyIt = url.searchParams.get('apply') === 'true'
      const out: { object: string; description: string; text: string }[] = []
      const blocked: string[] = []
      for (const e of model.entities) {
        if (!c.tables.has(e.name)) { out.push(...statements(model, schema, new Set([e.name]))); continue }
        for (const a of e.attributes) if (!c.columns.get(e.name)!.has(a.name)) out.push({ object: `${schema}.${e.name}.${a.name}`, description: `Add the column ${a.name}`, text: `ALTER TABLE ${schema}.${e.name} ADD COLUMN ${a.name} ${a.type} NULL` })
      }
      const upToDate = out.length === 0 && blocked.length === 0
      if (applyIt && blocked.length === 0) {
        for (const e of model.entities) { c.tables.add(e.name); const cols = c.columns.get(e.name) ?? new Set<string>(); for (const a of e.attributes) cols.add(a.name); c.columns.set(e.name, cols); if (!c.rows.has(e.name)) c.rows.set(e.name, []) }
        c.definition = definitionOf(model)
      }
      return json(route, { up_to_date: upToDate, applied: applyIt && blocked.length === 0, statements: out, blocked, notes: ['Making a column NOT NULL, dropping or retyping one are not applied.'] })
    }
    if (!c.created && (rest[0] === 'entities' || op === 'changes')) return json(route, { error: `The tables of '${name}' have not been made: ${schema}.${model.entities[0]?.name ?? ''} does not exist. Create the storage first.` }, 409)
    if (rest[0] === 'entities' && rest[2] === 'rows' && method === 'GET') {
      const list = (c.rows.get(rest[1]!) ?? []).slice().sort((a, b) => JSON.stringify(a.values).localeCompare(JSON.stringify(b.values)))
      const limit = Number(url.searchParams.get('limit') ?? 25)
      const offset = Number(url.searchParams.get('offset') ?? 0)
      return json(route, { rows: list.slice(offset, offset + limit), limit, offset })
    }
    if (rest[0] === 'entities' && rest[2] === 'row' && method === 'GET') {
      const entity = model.entities.find((e) => e.name === rest[1])
      if (!entity) return json(route, { error: `The model has no entity '${rest[1]}'.` }, 404)
      const key = Object.fromEntries(entity.key.map((k) => [k, url.searchParams.get(`key.${k}`)]))
      const found = (c.rows.get(entity.name) ?? []).find((r) => entity.key.every((k) => String(r.values[k]) === String(key[k])))
      if (url.searchParams.get('history') === 'true') return json(route, { versions: (c.history.get(entity.name) ?? []).filter((r) => entity.key.every((k) => String(r.values[k]) === String(key[k]))) })
      return found ? json(route, found) : json(route, { error: `\`${entity.name}\` has no row ${entity.key.map((k) => `${k}=${key[k]}`).join(', ')}.` }, 404)
    }
    if (op === 'changes' && method === 'POST') {
      if (!need(route, can.write(), 'author')) return
      const parsed = JSON.parse(body) as { changes: Change[]; description?: string }
      if (model.approval !== 'none') {
        const r: Request = { id: sha(String(requests.length) + body).slice(0, 32), model: name, instance, state: 'pending', proposed_by: 'console-user', proposed_by_name: 'You', by_agent: false, proposed_at: now(), description: parsed.description ?? null, changes: parsed.changes, decided_by: null, decided_at: null, note: null }
        requests.push(r)
        return json(route, requestJson(r, false), 202)
      }
      const refused = apply(model, c, parsed.changes)
      return refused ? json(route, refused.body, refused.status) : json(route, { applied: parsed.changes.length })
    }
    if (rest[0] === 'requests') {
      const mine = requests.filter((r) => r.model === name && r.instance === instance)
      if (rest.length === 1 && method === 'GET') {
        const state = url.searchParams.get('state')
        return json(route, { requests: mine.filter((r) => !state || r.state === state).map((r) => requestJson(r, false)), can_decide: mayDecide(model), decision_needs: approver(model), approval: model.approval })
      }
      const found = mine.find((r) => r.id === rest[1])
      if (!found) return json(route, { error: 'There is no such change request.' }, 404)
      if (rest.length === 2 && method === 'GET') return json(route, requestJson(found, true))
      if (rest.length === 3 && method === 'POST') {
        if (!need(route, mayDecide(model), approver(model))) return
        if (found.state !== 'pending') return json(route, { error: `This change request was already ${found.state}.` }, 409)
        const note = body ? (JSON.parse(body) as { note?: string }).note ?? null : null
        if (rest[2] === 'approve') {
          const refused = apply(model, c, found.changes)
          if (refused) return json(route, refused.body, refused.status)
          found.state = 'approved'
        } else found.state = 'rejected'
        found.decided_by = 'console-user'; found.decided_at = now(); found.note = note
        return json(route, requestJson(found, false))
      }
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })

  return {
    calls,
    text: (name) => texts.get(name),
    rows: (model, entity, instance = 'default') => (copyOf(model, instance).rows.get(entity) ?? []).map((r) => r.values),
    touch: (model, entity, key, values) => {
      const { model: parsed } = check(model, texts.get(model)!)
      const e = parsed!.entities.find((x) => x.name === entity)!
      const row = copyOf(model, 'default').rows.get(entity)!.find((r) => e.key.every((k) => String(r.values[k]) === String(key[k])))!
      row.values = { ...row.values, ...values }
      row.version += 1
    },
    propose: (model, changes, description) => {
      const r: Request = { id: sha(`agent${requests.length}${description}`).slice(0, 32), model, instance: 'default', state: 'pending', proposed_by: 'agent:bot', proposed_by_name: 'Territory bot', by_agent: true, proposed_at: now(), description, changes, decided_by: null, decided_at: null, note: null }
      requests.push(r)
      return r.id
    },
    edit: (name, text) => { texts.set(name, text) },
    setRole: (next) => { role = next },
  }
}
