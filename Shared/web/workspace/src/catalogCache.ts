import { parseYaml } from '@dbdatakit/shell'

/**
 * The catalog cache, format `catalog-cache/2`: the databases, schemas, tables, columns and keys of a project's connections, with where and when each connection was read (a Runner channel's `discover`, or a Gateway's schema call), written
 * into the draft as `catalog-cache.yaml`. It holds no secret and no row. This is the twin of `CatalogCache` (C#, `DbDataKit.Catalog.Cache`), which is the format's definition: both are run over the same fixtures
 * (`DbDataWorkspace/tests/DbDataWorkspace.Tests/Fixtures/catalog-cache`) and give the same code and key path for every finding. The screens use this one to read a cache without a round trip; the server's check is the one that decides.
 * Design: docs/authoring-metadata.md#the-catalog-cache
 */
export const CATALOG_CACHE_FILE = 'catalog-cache.yaml'
export const CATALOG_CACHE_VERSION = 2

export interface CacheColumn { name: string; type: string; nullable: boolean; key: boolean }
export interface CacheKey { name: string | null; columns: string[] }
export interface CacheForeignKey { name: string | null; columns: string[]; references: { database: string; schema: string; table: string; columns: string[] } }
export interface CacheTable { database: string; schema: string; name: string; kind: 'table' | 'view'; columns: CacheColumn[]; uniqueKeys: CacheKey[]; foreignKeys: CacheForeignKey[] }
export interface CacheDatabase { name: string; schemas: string[] }
/** Where and when a connection was read: the paired server by name and product, the channel whose binding gave the login (a Runner), and the time. */
export interface CacheReadAt { server: string; product: 'runner' | 'gateway'; channel: string | null; at: string }
export interface CacheConnection { engine: string; read: CacheReadAt | null; databases: CacheDatabase[]; tables: CacheTable[] }
export interface CatalogCache { generatedAt: string; connections: Record<string, CacheConnection> }
export interface CacheFinding { code: string; path: string; message: string }
export interface CacheRead { cache: CatalogCache | null; findings: CacheFinding[] }

export const MAX_TABLES = 20_000
export const MAX_COLUMNS = 5_000
export const MAX_DATABASES = 1_000
export const MAX_SCHEMAS = 10_000
const PRODUCTS = ['runner', 'gateway']

const TOP = ['schema', 'generated_at', 'connections']
const READ = ['server', 'product', 'channel', 'at']
const CONNECTION = ['engine', 'read', 'databases', 'tables']
const DATABASE = ['name', 'schemas']
const TABLE = ['database', 'schema', 'name', 'kind', 'columns', 'unique_keys', 'foreign_keys']
const COLUMN = ['name', 'type', 'nullable', 'key']
const UNIQUE = ['name', 'columns']
const FOREIGN = ['name', 'columns', 'references']
const REFERENCE = ['database', 'schema', 'table', 'columns']
const FORBIDDEN = new Set(['row', 'rows', 'value', 'values', 'sample', 'samples', 'data', 'preview', 'example', 'examples', 'password', 'passwd', 'pwd', 'secret', 'secrets', 'token', 'login', 'user', 'username', 'credentials', 'connection_string', 'connectionstring', 'url', 'address', 'host', 'port',
  'min', 'max', 'minimum', 'maximum', 'distinct', 'statistics', 'stats', 'histogram', 'bounds'])

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

/** `database.schema.table`, leaving out the parts that are empty. */
export const tableLabel = (t: { database?: string; schema: string; name: string }) => [t.database ?? '', t.schema, t.name].filter((p) => p !== '').join('.')

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
        ? `\`${key}\` has no place in a catalog cache: it holds the shape of tables, never a row, a value or a statistic of values, and never a secret or an address with a login.`
        : `\`${key}\` is not a key of a catalog cache here (known: ${known.join(', ')}).`)
    }
  }
  const value = (v: unknown, here: string, key: string): string | null => {
    const s = scalar(v)
    if (s === null || s.trim() === '') { fail('DBD-CAT-003', here, `\`${key}\` is a text, not empty.`); return null }
    const why = secretLook(s)
    if (why) { fail('DBD-CAT-004', here, `The value of \`${key}\` ${why}; a catalog cache holds no secret and no login.`); return null }
    return s
  }
  const text1 = (map: Record<string, unknown>, key: string, path: string, required = true): string | null => {
    const here = join(path, key)
    if (!(key in map)) { if (required) fail('DBD-CAT-003', here, `\`${key}\` is missing.`); return null }
    return value(map[key], here, key)
  }
  const names = (map: Record<string, unknown>, key: string, path: string, nonEmpty: boolean, max: number): string[] | null => {
    const here = join(path, key)
    if (!(key in map)) { fail('DBD-CAT-003', here, `\`${key}\` is missing.`); return null }
    const list = map[key]
    if (!Array.isArray(list) || (nonEmpty && list.length === 0)) { fail('DBD-CAT-003', here, nonEmpty ? `\`${key}\` is a list of at least one name.` : `\`${key}\` is a list of names.`); return null }
    if (list.length > max) fail('DBD-CAT-006', here, `\`${key}\` holds at most ${max} names in a cache.`)
    const out: string[] = []
    list.slice(0, max).forEach((item, i) => {
      const name = value(item, `${here}[${i}]`, key)
      if (name === null) return
      if (out.includes(name)) { fail('DBD-CAT-005', `${here}[${i}]`, `\`${name}\` is listed twice in \`${key}\`.`); return }
      out.push(name)
    })
    return out
  }
  const flag = (map: Record<string, unknown>, key: string, path: string, otherwise: boolean): boolean => {
    if (!(key in map)) return otherwise
    const v = map[key]
    if (v === true || v === 'true') return true
    if (v === false || v === 'false') return false
    fail('DBD-CAT-003', join(path, key), `\`${key}\` is true or false.`)
    return otherwise
  }
  const time = (map: Record<string, unknown>, key: string, path: string): string | null => {
    const v = text1(map, key, path)
    if (v !== null && Number.isNaN(Date.parse(v))) { fail('DBD-CAT-003', join(path, key), `\`${key}\` is a date and time (for example 2026-11-11T10:00:00Z).`); return null }
    return v
  }
  const items = (map: Record<string, unknown>, key: string, path: string): Array<[Record<string, unknown>, string]> => {
    if (!(key in map)) return []
    const list = map[key]
    if (!Array.isArray(list)) { fail('DBD-CAT-003', join(path, key), `\`${key}\` is a list.`); return [] }
    const out: Array<[Record<string, unknown>, string]> = []
    list.forEach((item, i) => {
      const here = `${join(path, key)}[${i}]`
      if (!isMap(item)) fail('DBD-CAT-003', here, 'A key is a mapping.')
      else out.push([item, here])
    })
    return out
  }

  const root = parsed.value
  if (!isMap(root)) { fail('DBD-CAT-003', '', 'A catalog cache file is a mapping.'); return { cache: null, findings } }
  const schema = text1(root, 'schema', '')
  if (schema !== null) {
    const m = /^catalog-cache\/(\d+)$/.exec(schema)
    if (!m || Number(m[1]) < 1) fail('DBD-CAT-001', 'schema', `\`schema\` is \`catalog-cache/${CATALOG_CACHE_VERSION}\`, not \`${schema}\`.`)
    else if (Number(m[1]) > CATALOG_CACHE_VERSION) {
      fail('DBD-CAT-001', 'schema', `${CATALOG_CACHE_FILE} is in format ${schema}, which is newer than this reads (catalog-cache/${CATALOG_CACHE_VERSION}); update the software or make the cache again.`)
      return { cache: null, findings }
    } else if (Number(m[1]) < CATALOG_CACHE_VERSION) {
      fail('DBD-CAT-001', 'schema', `${CATALOG_CACHE_FILE} is in format ${schema}, which this no longer reads (it reads catalog-cache/${CATALOG_CACHE_VERSION}, which records where and when each connection was read, its databases and schemas, and foreign keys); delete the file and refresh the catalog of each connection to make it again.`)
      return { cache: null, findings }
    }
  }
  strict(root, '', TOP)
  const generated = time(root, 'generated_at', '')

  const connections: Record<string, CacheConnection> = {}
  if ('connections' in root) {
    if (!isMap(root.connections)) fail('DBD-CAT-003', 'connections', '`connections` is a mapping.')
    else {
      for (const [name, entry] of Object.entries(root.connections)) {
        const path = `connections.${name}`
        if (!isMap(entry)) { fail('DBD-CAT-003', path, 'A connection is a mapping.'); continue }
        strict(entry, path, CONNECTION)
        const engine = text1(entry, 'engine', path)

        let read: CacheReadAt | null = null
        if (!('read' in entry)) fail('DBD-CAT-003', `${path}.read`, '`read` is missing: it says which paired server read the connection, in which channel, and when.')
        else if (!isMap(entry.read)) fail('DBD-CAT-003', `${path}.read`, '`read` is a mapping.')
        else {
          const r = entry.read
          const here = `${path}.read`
          strict(r, here, READ)
          const server = text1(r, 'server', here)
          let product = text1(r, 'product', here)
          const channel = text1(r, 'channel', here, false)
          const at = time(r, 'at', here)
          if (product !== null && !PRODUCTS.includes(product)) { fail('DBD-CAT-003', `${here}.product`, `\`product\` is ${PRODUCTS.join(' or ')}, not \`${product}\`.`); product = null }
          if (product === 'runner' && channel === null && !('channel' in r)) fail('DBD-CAT-003', `${here}.channel`, '`channel` is missing: a Runner reads with the login a channel binds, and the cache says which.')
          if (server !== null && product !== null && at !== null) read = { server, product: product as 'runner' | 'gateway', channel, at }
        }

        const databases: CacheDatabase[] = []
        if (!('databases' in entry)) fail('DBD-CAT-003', `${path}.databases`, '`databases` is missing (an empty list when the connection\'s databases were not read).')
        else if (!Array.isArray(entry.databases)) fail('DBD-CAT-003', `${path}.databases`, '`databases` is a list.')
        else {
          if (entry.databases.length > MAX_DATABASES) fail('DBD-CAT-006', `${path}.databases`, `A connection holds at most ${MAX_DATABASES} databases in a cache.`)
          entry.databases.slice(0, MAX_DATABASES).forEach((d, i) => {
            const dpath = `${path}.databases[${i}]`
            if (!isMap(d)) { fail('DBD-CAT-003', dpath, 'A database is a mapping.'); return }
            strict(d, dpath, DATABASE)
            const dname = text1(d, 'name', dpath)
            const schemas = names(d, 'schemas', dpath, false, MAX_SCHEMAS) ?? []
            if (dname === null) return
            if (databases.some((x) => x.name === dname)) { fail('DBD-CAT-005', dpath, `The database \`${dname}\` is listed twice on \`${name}\`.`); return }
            databases.push({ name: dname, schemas })
          })
        }

        const tables: CacheTable[] = []
        if ('tables' in entry) {
          if (!Array.isArray(entry.tables)) fail('DBD-CAT-003', `${path}.tables`, '`tables` is a list.')
          else {
            if (entry.tables.length > MAX_TABLES) fail('DBD-CAT-006', `${path}.tables`, `A connection holds at most ${MAX_TABLES} tables in a cache; cache the schemas the project uses.`)
            const seen = new Set<string>()
            entry.tables.slice(0, MAX_TABLES).forEach((t, i) => {
              const tpath = `${path}.tables[${i}]`
              if (!isMap(t)) { fail('DBD-CAT-003', tpath, 'A table is a mapping.'); return }
              strict(t, tpath, TABLE)
              const tdatabase = text1(t, 'database', tpath, false) ?? ''
              const tschema = text1(t, 'schema', tpath, false) ?? ''
              const tname = text1(t, 'name', tpath)
              let kind = text1(t, 'kind', tpath)
              if (kind !== null && kind !== 'table' && kind !== 'view') { fail('DBD-CAT-003', `${tpath}.kind`, '`kind` is `table` or `view`.'); kind = null }
              const columns: CacheColumn[] = []
              if ('columns' in t) {
                if (!Array.isArray(t.columns) || t.columns.length === 0) fail('DBD-CAT-003', `${tpath}.columns`, '`columns` is a list with at least one column: a table with no columns has no shape to cache.')
                else {
                  if (t.columns.length > MAX_COLUMNS) fail('DBD-CAT-006', `${tpath}.columns`, `A table holds at most ${MAX_COLUMNS} columns in a cache.`)
                  const columnNames = new Set<string>()
                  t.columns.slice(0, MAX_COLUMNS).forEach((c, j) => {
                    const cpath = `${tpath}.columns[${j}]`
                    if (!isMap(c)) { fail('DBD-CAT-003', cpath, 'A column is a mapping.'); return }
                    strict(c, cpath, COLUMN)
                    const cname = text1(c, 'name', cpath)
                    const type = text1(c, 'type', cpath)
                    const nullable = flag(c, 'nullable', cpath, true)
                    const key = flag(c, 'key', cpath, false)
                    if (cname === null || type === null) return
                    if (columnNames.has(cname.toLowerCase())) fail('DBD-CAT-005', cpath, `The column \`${cname}\` is listed twice in \`${tname}\`.`)
                    columnNames.add(cname.toLowerCase())
                    columns.push({ name: cname, type, nullable, key })
                  })
                }
              } else fail('DBD-CAT-003', `${tpath}.columns`, '`columns` is missing.')
              const known = new Set(columns.map((c) => c.name.toLowerCase()))
              const listed = (cols: string[], at: string) => {
                for (const c of cols) if (!known.has(c.toLowerCase())) fail('DBD-CAT-007', at, `The key names the column \`${c}\`, which \`${tname}\` does not list.`)
              }
              const uniqueKeys: CacheKey[] = []
              for (const [k, kpath] of items(t, 'unique_keys', tpath)) {
                strict(k, kpath, UNIQUE)
                const kname = text1(k, 'name', kpath, false)
                const kcols = names(k, 'columns', kpath, true, MAX_COLUMNS)
                if (kcols === null) continue
                listed(kcols, `${kpath}.columns`)
                uniqueKeys.push({ name: kname, columns: kcols })
              }
              const foreignKeys: CacheForeignKey[] = []
              for (const [k, kpath] of items(t, 'foreign_keys', tpath)) {
                strict(k, kpath, FOREIGN)
                const kname = text1(k, 'name', kpath, false)
                const kcols = names(k, 'columns', kpath, true, MAX_COLUMNS)
                if (kcols !== null) listed(kcols, `${kpath}.columns`)
                const rpath = `${kpath}.references`
                if (!('references' in k)) { fail('DBD-CAT-003', rpath, '`references` is missing: the table and columns the key points at.'); continue }
                if (!isMap(k.references)) { fail('DBD-CAT-003', rpath, '`references` is a mapping.'); continue }
                const r = k.references
                strict(r, rpath, REFERENCE)
                const rdatabase = text1(r, 'database', rpath, false) ?? ''
                const rschema = text1(r, 'schema', rpath, false) ?? ''
                const rtable = text1(r, 'table', rpath)
                const rcols = names(r, 'columns', rpath, true, MAX_COLUMNS)
                if (kcols === null || rtable === null || rcols === null) continue
                if (rcols.length !== kcols.length) { fail('DBD-CAT-003', `${rpath}.columns`, `The foreign key has ${kcols.length} column(s) and references ${rcols.length}: they pair one to one.`); continue }
                foreignKeys.push({ name: kname, columns: kcols, references: { database: rdatabase, schema: rschema, table: rtable, columns: rcols } })
              }
              if (tname === null || kind === null) return
              const table: CacheTable = { database: tdatabase, schema: tschema, name: tname, kind: kind as 'table' | 'view', columns, uniqueKeys, foreignKeys }
              const id = `${tdatabase}\u0001${tschema}\u0001${tname}`
              if (seen.has(id)) fail('DBD-CAT-005', tpath, `The table \`${tableLabel(table)}\` is listed twice on \`${name}\`.`)
              seen.add(id)
              // a table names a listed database and one of its schemas when the connection lists databases, and none when it lists none
              if (databases.length === 0) {
                if (tdatabase !== '') fail('DBD-CAT-007', `${tpath}.database`, `\`${tableLabel(table)}\` names the database \`${tdatabase}\`, and the connection lists no databases.`)
              } else if (tdatabase === '') fail('DBD-CAT-007', `${tpath}.database`, `\`${tableLabel(table)}\` names no database, and the connection lists its databases: say which one it is in.`)
              else {
                const db = databases.find((d) => d.name === tdatabase)
                if (!db) fail('DBD-CAT-007', `${tpath}.database`, `\`${tableLabel(table)}\` is in the database \`${tdatabase}\`, which the connection does not list.`)
                else if (tschema !== '' && !db.schemas.includes(tschema)) fail('DBD-CAT-007', `${tpath}.schema`, `\`${tableLabel(table)}\` is in the schema \`${tschema}\`, which the database \`${tdatabase}\` does not list.`)
              }
              tables.push(table)
            })
          }
        } else fail('DBD-CAT-003', `${path}.tables`, '`tables` is missing.')
        connections[name] = { engine: engine ?? '', read, databases, tables }
      }
    }
  } else fail('DBD-CAT-003', 'connections', '`connections` is missing.')
  return { cache: findings.length === 0 ? { generatedAt: generated ?? '', connections } : null, findings }
}

/** Where and when a connection was read, in words: `read from runners-eu (runner) in dev at 2026-11-18T10:00:00Z`. */
export const readLine = (r: CacheReadAt): string => `read from ${r.server} (${r.product})${r.channel !== null ? ` in ${r.channel}` : ''} at ${r.at}`

/** The schemas of a connection, the empty ones too: those its databases list, else those its tables are in. */
export function schemasOf(connection: CacheConnection, database?: string): string[] {
  const listed = connection.databases.filter((d) => database === undefined || d.name === database).flatMap((d) => d.schemas)
  const from = listed.length > 0 || connection.databases.length > 0 ? listed : connection.tables.map((t) => t.schema).filter((s) => s !== '')
  return [...new Set(from)].sort()
}

const q = (text: string): string => {
  // a control character cannot be a line of the file (the C# writer refuses it too)
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(text)) throw new Error('A name in the catalog has a control character, which a catalog cache cannot hold.')
  return `'${text.replace(/'/g, "''")}'`
}
const list = (names: string[]) => `[${names.map(q).join(', ')}]`
const byText = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0)

/** The file's text for a cache, in the same order and quoting as the C# writer (`CatalogCache.Write`), so a cache written in the browser and one written by the server are the same text. */
export function writeCatalogCache(cache: CatalogCache): string {
  const out: string[] = [`schema: catalog-cache/${CATALOG_CACHE_VERSION}`, `generated_at: ${q(cache.generatedAt)}`]
  const names = Object.keys(cache.connections).sort(byText)
  if (names.length === 0) return [...out, 'connections: {}', ''].join('\n')
  out.push('connections:')
  for (const name of names) {
    const c = cache.connections[name]!
    out.push(`  ${q(name)}:`, `    engine: ${q(c.engine)}`)
    if (c.read) out.push(`    read: {server: ${q(c.read.server)}, product: ${q(c.read.product)}${c.read.channel !== null ? `, channel: ${q(c.read.channel)}` : ''}, at: ${q(c.read.at)}}`)
    if (c.databases.length === 0) out.push('    databases: []')
    else {
      out.push('    databases:')
      for (const d of [...c.databases].sort((a, b) => byText(a.name, b.name))) out.push(`      - {name: ${q(d.name)}, schemas: ${list([...d.schemas].sort(byText))}}`)
    }
    if (c.tables.length === 0) { out.push('    tables: []'); continue }
    out.push('    tables:')
    const sorted = [...c.tables].sort((a, b) => byText(a.database, b.database) || byText(a.schema, b.schema) || byText(a.name, b.name))
    for (const t of sorted) {
      const lines: string[] = []
      if (t.database !== '') lines.push(`database: ${q(t.database)}`)
      if (t.schema !== '') lines.push(`schema: ${q(t.schema)}`)
      lines.push(`name: ${q(t.name)}`, `kind: ${t.kind}`)
      lines.forEach((l, i) => out.push(`${i === 0 ? '      - ' : '        '}${l}`))
      out.push('        columns:')
      for (const col of t.columns) out.push(`          - {name: ${q(col.name)}, type: ${q(col.type)}, nullable: ${col.nullable ? 'true' : 'false'}${col.key ? ', key: true' : ''}}`)
      if (t.uniqueKeys.length > 0) {
        out.push('        unique_keys:')
        for (const k of t.uniqueKeys) out.push(`          - {${k.name !== null ? `name: ${q(k.name)}, ` : ''}columns: ${list(k.columns)}}`)
      }
      if (t.foreignKeys.length > 0) {
        out.push('        foreign_keys:')
        for (const k of t.foreignKeys) {
          const r = k.references
          out.push(`          - {${k.name !== null ? `name: ${q(k.name)}, ` : ''}columns: ${list(k.columns)}, references: {${r.database !== '' ? `database: ${q(r.database)}, ` : ''}${r.schema !== '' ? `schema: ${q(r.schema)}, ` : ''}table: ${q(r.table)}, columns: ${list(r.columns)}}}`)
        }
      }
    }
  }
  return [...out, ''].join('\n')
}
