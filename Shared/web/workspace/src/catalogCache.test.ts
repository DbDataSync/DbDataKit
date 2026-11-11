import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { readCatalogCache, secretLook, tableLabel } from './catalogCache'

// The fixtures the C# reader (`CatalogCacheTests`) runs over: both readers give the same code and key path for every finding, so the two cannot drift apart.
const folder = fileURLToPath(new URL('../../../../DbDataWorkspace/tests/DbDataWorkspace.Tests/Fixtures/catalog-cache/', import.meta.url))
const expected = JSON.parse(readFileSync(folder + 'expected.json', 'utf8')) as Record<string, string[]>

describe('the catalog cache reader (the twin of the C# one)', () => {
  it('gives the findings every fixture lists, by code and key path', () => {
    expect(Object.keys(expected).length).toBeGreaterThanOrEqual(7)
    for (const [file, findings] of Object.entries(expected)) {
      const got = readCatalogCache(readFileSync(folder + file, 'utf8')).findings.map((f) => `${f.code} ${f.path}`).sort()
      expect({ file, got }).toEqual({ file, got: [...findings].sort() })
    }
  })

  it('reads a sound cache whole', () => {
    const { cache, findings } = readCatalogCache(readFileSync(folder + 'sound.yaml', 'utf8'))
    expect(findings).toEqual([])
    expect(cache!.server).toBe('prod-gateway')
    const orders = cache!.connections['source-db']!.tables[0]!
    expect(tableLabel(orders)).toBe('public.orders')
    expect(orders.columns.map((c) => [c.name, c.nullable, c.key])).toEqual([['id', false, true], ['customer_id', false, false], ['placed_at', true, false], ['password', true, false]])
    expect(cache!.connections['source-db']!.tables[1]!.kind).toBe('view')
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
