import { parseYaml } from '@dbdatakit/shell'

/**
 * The catalog cache, format `catalog-cache/1` (plan 33 D20, SC20i): the shape of the tables of a project's connections, written into the draft as `catalog-cache.yaml` by a paired server that holds the logins. It holds no secret and
 * no row. This is the twin of `CatalogCache` (C#, `DbDataKit.Authoring.Server`), which is the format's definition: both are run over the same fixtures (`DbDataWorkspace/tests/DbDataWorkspace.Tests/Fixtures/catalog-cache`) and give
 * the same code and key path for every finding. The screens use this one to read a cache without a round trip; the server's check is the one that decides.
 */
export const CATALOG_CACHE_FILE = 'catalog-cache.yaml'
export const CATALOG_CACHE_VERSION = 1

export interface CacheColumn { name: string; type: string; nullable: boolean; key: boolean }
export interface CacheTable { schema: string; name: string; kind: 'table' | 'view'; columns: CacheColumn[] }
export interface CacheConnection { engine: string; tables: CacheTable[] }
export interface CatalogCache { generatedAt: string; server: string; product: string; connections: Record<string, CacheConnection> }
export interface CacheFinding { code: string; path: string; message: string }
export interface CacheRead { cache: CatalogCache | null; findings: CacheFinding[] }

export const MAX_TABLES = 20_000
export const MAX_COLUMNS = 5_000

const TOP = ['schema', 'generated_at', 'source', 'connections']
const SOURCE = ['server', 'product']
const CONNECTION = ['engine', 'tables']
const TABLE = ['schema', 'name', 'kind', 'columns']
const COLUMN = ['name', 'type', 'nullable', 'key']
const FORBIDDEN = new Set(['row', 'rows', 'value', 'values', 'sample', 'samples', 'data', 'preview', 'example', 'examples', 'password', 'passwd', 'pwd', 'secret', 'secrets', 'token', 'login', 'user', 'username', 'credentials', 'connection_string', 'connectionstring', 'url', 'address', 'host', 'port'])

/** Why a value cannot be in the cache, or null (the same four looks as the C# reader). */
export function secretLook(value: string): string | null {
  if (/\b(password|passwd|pwd|secret|token|api[_-]?key)\s*[=:]\s*\S/i.test(value)) return 'looks like a password or a token being set'
  if (/[A-Za-z][\w ]*=[^;=]+;\s*[A-Za-z][\w ]*=/.test(value)) return 'looks like a connection string'
  if (/[a-z][a-z0-9+.-]*:\/\/[^/\s@]*@/i.test(value)) return 'looks like an address with a login in it'
  if (/^(secret|env|file):\S/i.test(value)) return 'is a reference to a secret'
  return null
}

const isMap = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const scalar = (v: unknown): string | null => (typeof v === 'string' ? v : typeof v === 'number' || typeof v === 'boolean' ? String(v) : null)

export function readCatalogCache(text: string): CacheRead {
  const findings: CacheFinding[] = []
  const fail = (code: string, path: string, message: string) => findings.push({ code, path, message })
  const parsed = parseYaml(text)
  if (parsed.errors.length > 0) return { cache: null, findings: [{ code: 'DBD-101', path: '', message: parsed.errors[0] }] }
  const join = (path: string, key: string) => (path === '' ? key : `${path}.${key}`)
  const strict = (map: Record<string, unknown>, path: string, known: string[]) => {
    for (const key of Object.keys(map)) {
      if (known.includes(key)) continue
      fail('DBD-CAT-002', join(path, key), FORBIDDEN.has(key.toLowerCase())
        ? `\`${key}\` has no place in a catalog cache: it holds the shape of tables, never a row and never a secret or an address with a login.`
        : `\`${key}\` is not a key of a catalog cache here (known: ${known.join(', ')}).`)
    }
  }
  const text1 = (map: Record<string, unknown>, key: string, path: string, required = true): string | null => {
    const here = join(path, key)
    if (!(key in map)) { if (required) fail('DBD-CAT-003', here, `\`${key}\` is missing.`); return null }
    const s = scalar(map[key])
    if (s === null || s.trim() === '') { fail('DBD-CAT-003', here, `\`${key}\` is a text, not empty.`); return null }
    const why = secretLook(s)
    if (why) { fail('DBD-CAT-004', here, `The value of \`${key}\` ${why}; a catalog cache holds no secret and no login.`); return null }
    return s
  }
  const flag = (map: Record<string, unknown>, key: string, path: string, otherwise: boolean): boolean => {
    if (!(key in map)) return otherwise
    const v = map[key]
    if (v === true || v === 'true') return true
    if (v === false || v === 'false') return false
    fail('DBD-CAT-003', join(path, key), `\`${key}\` is true or false.`)
    return otherwise
  }
  const root = parsed.value
  if (!isMap(root)) { fail('DBD-CAT-003', '', 'A catalog cache file is a mapping.'); return { cache: null, findings } }
  strict(root, '', TOP)
  const schema = text1(root, 'schema', '')
  if (schema !== null) {
    const m = /^catalog-cache\/(\d+)$/.exec(schema)
    if (!m) fail('DBD-CAT-001', 'schema', `\`schema\` is \`catalog-cache/${CATALOG_CACHE_VERSION}\`, not \`${schema}\`.`)
    else if (Number(m[1]) > CATALOG_CACHE_VERSION) fail('DBD-CAT-001', 'schema', `${CATALOG_CACHE_FILE} is in format ${schema}, which is newer than this reads (catalog-cache/${CATALOG_CACHE_VERSION}); update the software or make the cache again.`)
    else if (Number(m[1]) < 1) fail('DBD-CAT-001', 'schema', `\`schema\` is \`catalog-cache/${CATALOG_CACHE_VERSION}\`, not \`${schema}\`.`)
  }
  const generated = text1(root, 'generated_at', '')
  if (generated !== null && Number.isNaN(Date.parse(generated))) fail('DBD-CAT-003', 'generated_at', '`generated_at` is a date and time (for example 2026-11-11T10:00:00Z).')
  let server = ''
  let product = ''
  if ('source' in root) {
    if (!isMap(root.source)) fail('DBD-CAT-003', 'source', '`source` is a mapping.')
    else { strict(root.source, 'source', SOURCE); server = text1(root.source, 'server', 'source') ?? ''; product = text1(root.source, 'product', 'source') ?? '' }
  } else fail('DBD-CAT-003', 'source', '`source` is missing: it names the paired server the answer came from.')

  const connections: Record<string, CacheConnection> = {}
  if ('connections' in root) {
    if (!isMap(root.connections)) fail('DBD-CAT-003', 'connections', '`connections` is a mapping.')
    else {
      for (const [name, value] of Object.entries(root.connections)) {
        const path = `connections.${name}`
        if (!isMap(value)) { fail('DBD-CAT-003', path, 'A connection is a mapping.'); continue }
        strict(value, path, CONNECTION)
        const engine = text1(value, 'engine', path)
        const tables: CacheTable[] = []
        if ('tables' in value) {
          if (!Array.isArray(value.tables)) fail('DBD-CAT-003', `${path}.tables`, '`tables` is a list.')
          else {
            if (value.tables.length > MAX_TABLES) fail('DBD-CAT-006', `${path}.tables`, `A connection holds at most ${MAX_TABLES} tables in a cache; cache the schemas the project uses.`)
            const seen = new Set<string>()
            value.tables.slice(0, MAX_TABLES).forEach((t, i) => {
              const tpath = `${path}.tables[${i}]`
              if (!isMap(t)) { fail('DBD-CAT-003', tpath, 'A table is a mapping.'); return }
              strict(t, tpath, TABLE)
              const tschema = text1(t, 'schema', tpath, false) ?? ''
              const tname = text1(t, 'name', tpath)
              let kind = text1(t, 'kind', tpath)
              if (kind !== null && kind !== 'table' && kind !== 'view') { fail('DBD-CAT-003', `${tpath}.kind`, '`kind` is `table` or `view`.'); kind = null }
              const columns: CacheColumn[] = []
              if ('columns' in t) {
                if (!Array.isArray(t.columns) || t.columns.length === 0) fail('DBD-CAT-003', `${tpath}.columns`, '`columns` is a list with at least one column: a table with no columns has no shape to cache.')
                else {
                  if (t.columns.length > MAX_COLUMNS) fail('DBD-CAT-006', `${tpath}.columns`, `A table holds at most ${MAX_COLUMNS} columns in a cache.`)
                  const names = new Set<string>()
                  t.columns.slice(0, MAX_COLUMNS).forEach((c, j) => {
                    const cpath = `${tpath}.columns[${j}]`
                    if (!isMap(c)) { fail('DBD-CAT-003', cpath, 'A column is a mapping.'); return }
                    strict(c, cpath, COLUMN)
                    const cname = text1(c, 'name', cpath)
                    const type = text1(c, 'type', cpath)
                    const nullable = flag(c, 'nullable', cpath, true)
                    const key = flag(c, 'key', cpath, false)
                    if (cname === null || type === null) return
                    if (names.has(cname.toLowerCase())) fail('DBD-CAT-005', cpath, `The column \`${cname}\` is listed twice in \`${tname}\`.`)
                    names.add(cname.toLowerCase())
                    columns.push({ name: cname, type, nullable, key })
                  })
                }
              } else fail('DBD-CAT-003', `${tpath}.columns`, '`columns` is missing.')
              if (tname === null || kind === null) return
              if (seen.has(`${tschema}\u0001${tname}`)) fail('DBD-CAT-005', tpath, `The table \`${tschema}.${tname}\` is listed twice on \`${name}\`.`)
              seen.add(`${tschema}\u0001${tname}`)
              tables.push({ schema: tschema, name: tname, kind: kind as 'table' | 'view', columns })
            })
          }
        } else fail('DBD-CAT-003', `${path}.tables`, '`tables` is missing.')
        connections[name] = { engine: engine ?? '', tables }
      }
    }
  } else fail('DBD-CAT-003', 'connections', '`connections` is missing.')
  return { cache: findings.length === 0 ? { generatedAt: generated ?? '', server, product, connections } : null, findings }
}

/** `schema.table`, or just the table when it has no schema. */
export const tableLabel = (t: { schema: string; name: string }) => (t.schema ? `${t.schema}.${t.name}` : t.name)
