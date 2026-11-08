import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import coreSchema from '../../schemas/core.schema.json'
import dashboardSchema from '../../schemas/dashboard.schema.json'
import reportSchema from '../../schemas/report.schema.json'
import { VIEWS_FORMAT, validateDashboard, validateReport, validateViewsDocument } from './document'
import type { ViewKind } from './document'
import type { DrillTarget } from './widgets'

/** The documents and goldens shared with the .NET suite (`DbDataKit.Views.Tests`): `DbDataKit/conformance/views`. */
const root = join(import.meta.dirname, '../../../../conformance/views')
const read = (relative: string) => JSON.parse(readFileSync(join(root, relative), 'utf8')) as Record<string, any>
const cases = read('cases.json') as {
  good: { file: string; kind: ViewKind }[]
  bad: { name: string; file: string; as: ViewKind | null; code: string; path: string; line: number; targets?: Record<string, DrillTarget[]>; schema: boolean }[]
  warnings: { name: string; file: string; kind: ViewKind; code: string; path: string }[]
}
const errors = (issues: { severity: string }[]) => issues.filter((i) => i.severity === 'error')

describe('good documents', () => {
  for (const c of cases.good.filter((c) => c.file.endsWith('.json')))
    it(`${c.file} is valid`, () => {
      expect(validateViewsDocument(read(c.file), { kind: c.kind })).toEqual([])
    })
})

describe('bad documents', () => {
  for (const c of cases.bad)
    it(`${c.name} is refused as ${c.code} at ${c.path}`, () => {
      const issues = validateViewsDocument(read(c.file), { kind: c.as ?? undefined, file: c.file, targets: c.targets })
      expect(errors(issues).length).toBeGreaterThan(0)
      expect(issues.some((i) => i.severity === 'error' && i.code === c.code && i.path === c.path), JSON.stringify(issues, null, 1)).toBe(true)
      expect(errors(issues).length, JSON.stringify(issues, null, 1)).toBeLessThanOrEqual(4)
    })
})

describe('documents that load with a warning', () => {
  for (const c of cases.warnings)
    it(`${c.name} warns ${c.code}`, () => {
      const issues = validateViewsDocument(read(c.file), { kind: c.kind })
      expect(issues).toHaveLength(1)
      expect(issues[0]).toMatchObject({ severity: 'warning', code: c.code, path: c.path })
    })
})

describe('the kinds', () => {
  it('each validator says what a file of the other kind is', () => {
    expect(validateReport(read('good/dashboard-layouts.json'))[0]).toMatchObject({ code: 'DBD-VIEW-001', path: 'kind' })
    expect(validateDashboard(read('good/report-minimal.json'))[0]).toMatchObject({ code: 'DBD-VIEW-001', path: 'kind' })
    expect(validateViewsDocument(read('good/report-minimal.json'))).toEqual([])
  })
  it('a newer format is refused naming the kind, the file and both numbers', () => {
    const [issue] = validateViewsDocument(read('bad/newer-format.json'), { file: 'views/old.json' })
    expect(issue!.code).toBe('DBD-110')
    for (const part of ['dashboard', 'views/old.json', 'format 2', 'format 1']) expect(issue!.message).toContain(part)
  })
})

describe('the schemas', () => {
  it('both kinds reference one core file and carry the one format number', () => {
    const refs = (schema: unknown) => [...JSON.stringify(schema).matchAll(/"\$ref":"([^"]+)"/g)].map((m) => m[1]!)
    for (const schema of [dashboardSchema, reportSchema]) {
      expect(refs(schema).length).toBeGreaterThan(0)
      for (const r of refs(schema)) expect(r.startsWith(`${coreSchema.$id}#/$defs/`)).toBe(true)
      expect(schema.properties.schemaVersion.const).toBe(VIEWS_FORMAT.current)
    }
    for (const shared of ['title', 'parameters', 'theme', 'widgets', 'layouts'] as const)
      expect((dashboardSchema.properties as any)[shared].$ref).toBe((reportSchema.properties as any)[shared].$ref)
  })
  it('the lock names the format number and the hashes of the three files', async () => {
    const { createHash } = await import('node:crypto')
    const lock = JSON.parse(readFileSync(join(import.meta.dirname, '../../schemas/views-format.lock.json'), 'utf8'))
    expect(lock.format).toBe(VIEWS_FORMAT.current)
    for (const name of ['core', 'dashboard', 'report'])
      expect(createHash('sha256').update(readFileSync(join(import.meta.dirname, `../../schemas/${name}.schema.json`), 'utf8').replace(/\r\n/g, '\n')).digest('hex'), `${name}.schema.json changed: see the lock`).toBe(lock.sha256[name])
  })
})

