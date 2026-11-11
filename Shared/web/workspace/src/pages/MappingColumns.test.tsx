import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { parseYaml } from '@dbdatakit/shell'
import { readCatalogCache } from '../catalogCache'
import { choicesOfMapping, findCachedTable, suggestPairs, tablesOfMapping, unknownSources, writeColumns } from '../mappingColumns'
import { MappingColumns } from './MappingColumns'

const cacheText = [
  'schema: catalog-cache/1', 'generated_at: 2026-11-11T10:00:00Z', 'source: {server: prod, product: gateway}', 'connections:',
  '  source-db:', '    engine: sqlserver', '    tables:',
  '      - {schema: dbo, name: Orders, kind: table, columns: [{name: Id, type: bigint, nullable: false, key: true}, {name: CustomerId, type: int}, {name: Total, type: "decimal(12,2)"}]}',
  '  target-db:', '    engine: postgres', '    tables:',
  '      - {schema: public, name: orders, kind: table, columns: [{name: id, type: bigint, nullable: false, key: true}, {name: customer_id, type: integer}, {name: total, type: numeric}, {name: loaded_at, type: timestamp}]}', '',
].join('\n')
const mapping = [
  'schemaVersion: 1', 'name: orders', '# the first table of the set', 'sources:', '- connectionName: source-db', '  database: App', '  schema: dbo', '  table: Orders',
  'targets:', '- connectionName: target-db', '  schema: public', '  table: orders', 'columnMappings: []', 'primaryKey: []', 'sourceColumns: []', 'targetColumns: []', '',
].join('\n')
const catalog = readCatalogCache(cacheText)

describe('the tables a mapping names, found in the cache', () => {
  it('reads the first source and target and says when there are more', () => {
    expect(tablesOfMapping(mapping)).toEqual({ source: { connection: 'source-db', schema: 'dbo', table: 'Orders' }, target: { connection: 'target-db', schema: 'public', table: 'orders' }, more: false })
    expect(tablesOfMapping(mapping.replace("targets:\n", "targets:\n- connectionName: target-db\n  table: archive\n"))!.more).toBe(true)
    expect(tablesOfMapping('name: x\n')).toBeNull()
    expect(tablesOfMapping('a: [1')).toBeNull()
  })

  it('finds a table without regard to case, and says what is missing in words', () => {
    const cache = catalog.cache!
    expect(findCachedTable(cache, { connection: 'source-db', schema: 'DBO', table: 'orders' }).table?.name).toBe('Orders')
    expect(findCachedTable(cache, { connection: 'source-db', schema: '', table: 'orders' }).table?.name).toBe('Orders')
    expect(findCachedTable(cache, { connection: 'nope', schema: '', table: 'orders' }).why).toMatch(/no connection called nope \(it has source-db, target-db\)/)
    expect(findCachedTable(cache, { connection: 'source-db', schema: 'dbo', table: 'missing' }).why).toMatch(/no table dbo.missing on source-db/)
  })
})

describe('the choices of a mapping', () => {
  const source = findCachedTable(catalog.cache!, { connection: 'source-db', schema: 'dbo', table: 'Orders' }).table!
  const target = findCachedTable(catalog.cache!, { connection: 'target-db', schema: 'public', table: 'orders' }).table!

  it('suggests the same-name columns whatever their case, and leaves the rest unfed', () => {
    expect(suggestPairs(source.columns, target.columns)).toEqual({ id: 'Id', total: 'Total' })
  })

  it('writes the mappings in the target\'s order, the key and the column cache with native types and key ordinals, and nothing else', () => {
    const out = writeColumns(mapping, { sourceOf: { id: 'Id', customer_id: 'CustomerId', total: 'Total' }, key: ['id'] }, source, target)
    expect(out).toContain('# the first table of the set')
    const doc = parseYaml(out).value as Record<string, unknown>
    expect(doc.columnMappings).toEqual([{ sourceColumn: 'Id', targetColumn: 'id' }, { sourceColumn: 'CustomerId', targetColumn: 'customer_id' }, { sourceColumn: 'Total', targetColumn: 'total' }])
    expect(doc.primaryKey).toEqual(['id'])
    expect(doc.sourceColumns).toEqual([{ name: 'Id', nativeType: 'bigint', isPrimaryKey: true, keyOrdinal: 1 }, { name: 'CustomerId', nativeType: 'int', isPrimaryKey: false }, { name: 'Total', nativeType: 'decimal(12,2)', isPrimaryKey: false }])
    expect((doc.targetColumns as { name: string }[]).map((c) => c.name)).toEqual(['id', 'customer_id', 'total', 'loaded_at'])
    expect(doc.sources).toEqual((parseYaml(mapping).value as Record<string, unknown>).sources)
    expect(choicesOfMapping(out)).toEqual({ sourceOf: { id: 'Id', customer_id: 'CustomerId', total: 'Total' }, key: ['id'] })
  })

  it('leaves out a target column that is not fed and names a source column the cache does not have', () => {
    const out = writeColumns(mapping, { sourceOf: { id: 'Id', loaded_at: '' }, key: ['id', 'ghost'] }, source, target)
    expect((parseYaml(out).value as Record<string, unknown>).columnMappings).toEqual([{ sourceColumn: 'Id', targetColumn: 'id' }])
    expect((parseYaml(out).value as Record<string, unknown>).primaryKey).toEqual(['id'])
    expect(unknownSources({ sourceOf: { id: 'Id', total: 'Gone' }, key: [] }, source.columns)).toEqual(['total'])
  })
})

describe('the mapping column editor', () => {
  const draw = (over: Partial<Parameters<typeof MappingColumns>[0]> = {}) => renderToStaticMarkup(<MappingColumns mapping={mapping} catalog={catalog} readOnly={false} onApply={() => {}} {...over} />)

  it('offers a row per target column with the same-name source chosen and the cached key marked, before anything is written', () => {
    const html = draw()
    expect(html.match(/data-testid="wsp-mapcols-row"/g)).toHaveLength(4)
    expect(html).toContain('data-written="false"')
    expect(html).toContain('<option value="Id" selected="">Id (bigint)</option>')
    expect(html).toMatch(/aria-label="id is part of the key"[^>]*checked/)
    expect(html).toContain('Write into the mapping')
  })

  it('shows what the mapping already says as written, and a reader gets no buttons', () => {
    const written = writeColumns(mapping, { sourceOf: { id: 'Id' }, key: ['id'] }, findCachedTable(catalog.cache!, { connection: 'source-db', schema: 'dbo', table: 'Orders' }).table!, findCachedTable(catalog.cache!, { connection: 'target-db', schema: 'public', table: 'orders' }).table!)
    expect(draw({ mapping: written })).toContain('data-written="true"')
    const reader = draw({ readOnly: true })
    expect(reader).not.toContain('wsp-mapcols-write')
    expect(reader).toContain('disabled')
  })

  it('says why there is nothing to pick from: no cache, a damaged one, no table named, a table the cache lacks', () => {
    expect(draw({ catalog: null })).toContain('There is no catalog cache in this draft')
    expect(draw({ catalog: readCatalogCache(cacheText.replace('key: true}', 'key: true, rows: []}')) })).toContain('has findings')
    expect(draw({ mapping: 'name: x\n' })).toContain('names no source and target table')
    expect(draw({ mapping: mapping.replace('table: Orders', 'table: Nothing') })).toContain('no table dbo.Nothing on source-db')
  })
})
