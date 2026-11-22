import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { readCatalogCache, readLine, schemasOf, secretLook, tableLabel, writeCatalogCache } from './catalogCache'

// The fixtures the C# reader (`CatalogCacheTests`) runs over: both readers give the same code and key path for every finding, so the two cannot drift apart.
const folder = fileURLToPath(new URL('../../../../DbDataWorkspace/tests/DbDataWorkspace.Tests/Fixtures/catalog-cache/', import.meta.url))
const expected = JSON.parse(readFileSync(folder + 'expected.json', 'utf8')) as Record<string, string[]>

describe('the catalog cache reader (the twin of the C# one)', () => {
  it('gives the findings every fixture lists, by code and key path', () => {
    expect(Object.keys(expected).length).toBeGreaterThanOrEqual(9)
    for (const [file, findings] of Object.entries(expected)) {
      const got = readCatalogCache(readFileSync(folder + file, 'utf8')).findings.map((f) => `${f.code} ${f.path}`).sort()
      expect({ file, got }).toEqual({ file, got: [...findings].sort() })
    }
  })

  it('reads a sound cache whole', () => {
    const { cache, findings } = readCatalogCache(readFileSync(folder + 'sound.yaml', 'utf8'))
    expect(findings).toEqual([])
    const source = cache!.connections['source-db']!
    expect(source.read).toEqual({ server: 'runners-eu', product: 'runner', channel: 'dev', at: '2026-11-18T10:00:00Z' })
    expect(readLine(source.read!)).toBe('read from runners-eu (runner) in dev at 2026-11-18T10:00:00Z')
    expect(cache!.connections['target-db']!.read!.channel).toBeNull()
    expect(source.databases).toEqual([{ name: 'shop', schemas: ['public', 'archive'] }])
    expect(schemasOf(source)).toEqual(['archive', 'public'])
    expect(schemasOf(cache!.connections['target-db']!)).toEqual(['dbo'])
    const orders = source.tables[0]!
    expect(tableLabel(orders)).toBe('shop.public.orders')
    expect(orders.columns.map((c) => [c.name, c.nullable, c.key])).toEqual([['id', false, true], ['customer_id', false, false], ['placed_at', true, false], ['password', true, false]])
    expect(orders.uniqueKeys).toEqual([{ name: 'uq_orders_customer_placed', columns: ['customer_id', 'placed_at'] }])
    expect(orders.foreignKeys).toEqual([{ name: 'fk_orders_customers', columns: ['customer_id'], references: { database: '', schema: 'public', table: 'customers', columns: ['id'] } }])
    expect(source.tables[1]!.kind).toBe('view')
  })

  it('writes the same text as the C# writer, which reads back the same', () => {
    const { cache } = readCatalogCache(readFileSync(folder + 'sound.yaml', 'utf8'))
    const text = writeCatalogCache(cache!)
    expect(text).toBe(readFileSync(folder + 'sound.written.txt', 'utf8').replace(/\r\n/g, '\n'))
    expect(readCatalogCache(text)).toEqual({ cache: readCatalogCache(text).cache, findings: [] })
    expect(writeCatalogCache(readCatalogCache(text).cache!)).toBe(text)
  })

  it('refuses a file of format 1 naming the file and saying how to make it again', () => {
    const { cache, findings } = readCatalogCache(readFileSync(folder + 'format1.yaml', 'utf8'))
    expect(cache).toBeNull()
    expect(findings).toHaveLength(1)
    expect(findings[0]!.message).toContain('catalog-cache.yaml is in format catalog-cache/1')
    expect(findings[0]!.message).toContain('refresh the catalog')
  })

  it('refuses text that is not YAML, a list and an empty file without throwing', () => {
    expect(readCatalogCache('a: [1').cache).toBeNull()
    expect(readCatalogCache('- a\n').findings[0]!.code).toBe('DBD-CAT-003')
    expect(readCatalogCache('').cache).toBeNull()
  })

  it('knows what a secret looks like and lets a plain type through', () => {
    expect(secretLook('password=abc')).toMatch(/password/)
    expect(secretLook('Server=db;User Id=sa')).toMatch(/connection string/)
    expect(secretLook('postgres://ada:pw@host/db')).toMatch(/login/)
    expect(secretLook('env:DB_LOGIN')).toMatch(/reference/)
    expect(secretLook('character varying(60)')).toBeNull()
    expect(secretLook('password')).toBeNull()
  })
})
