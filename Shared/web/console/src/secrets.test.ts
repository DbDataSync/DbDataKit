import { describe, expect, it } from 'vitest'
import { counts, findings, type ServerSecrets } from './secrets'

const gateway: ServerSecrets = {
  server: 'gw1', serverName: 'Gateway prod', product: 'dbdatagateway', problem: null,
  overview: {
    product: 'dbdatagateway', provider: 'secret-store',
    secrets: [{ name: 'dbdata:gateway:ds:old', reference: 'secret:dbdata:gateway:ds:old', updated: '2026-11-01T10:00:00Z', used_by: [] }, { name: 'dbdata:gateway:ds:erp', reference: 'secret:dbdata:gateway:ds:erp', updated: null, used_by: ['erp'] }],
    references: [
      { reference: 'secret:dbdata:gateway:ds:erp', kind: 'secret', status: 'present', used_by: ['erp'] },
      { reference: 'secret:dbdata:gateway:ds:crm', kind: 'secret', status: 'missing', used_by: ['crm', 'crm-read'] },
      { reference: 'env:HR_PASSWORD', kind: 'env', status: 'outside', used_by: ['hr'] },
    ],
    missing: ['secret:dbdata:gateway:ds:crm'], unused: ['dbdata:gateway:ds:old'],
  },
}
const runner: ServerSecrets = { server: 'rn1', serverName: 'Runner', product: 'dbdatarunner', problem: null, overview: { product: 'dbdatarunner', provider: 'runner-store', secrets: [], references: [], missing: [], unused: [] } }
const silent: ServerSecrets = { server: 'rn2', serverName: 'Old runner', product: 'dbdatarunner', overview: null, problem: 'There is no such call.' }

describe('the findings of the secrets overview', () => {
  it('lists what needs somebody first: a reference nothing answers, then a secret nothing names, then a reference outside the store', () => {
    const rows = findings([gateway, runner])
    expect(rows.map((r) => r.kind)).toEqual(['missing', 'unused', 'outside'])
    expect(rows[0]).toMatchObject({ subject: 'secret:dbdata:gateway:ds:crm', detail: 'named by crm, crm-read', link: '/servers/gw1/secrets' })
    expect(rows[1]!.subject).toBe('dbdata:gateway:ds:old')
    expect(rows[2]).toMatchObject({ subject: 'env:HR_PASSWORD' })
  })

  it('links a runner row to its packages, and skips a server with no overview without stopping the others', () => {
    const withRunner: ServerSecrets = { ...runner, overview: { ...runner.overview!, references: [{ reference: 'secret:dbdata:sync:connection:crm:run', kind: 'secret', status: 'missing', used_by: ['sample/ok'] }], missing: ['x'] } }
    const rows = findings([silent, withRunner])
    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({ kind: 'missing', serverName: 'Runner', link: '/servers/rn1/packages' })
  })

  it('counts each kind and finds nothing in a clean server', () => {
    expect(counts(findings([gateway]))).toEqual({ missing: 1, unused: 1, outside: 1 })
    expect(findings([runner])).toEqual([])
  })

  it('carries no value: a row holds a reference or a name and a sentence, nothing else', () => {
    expect(JSON.stringify(findings([gateway]))).not.toContain('"value"')
  })
})
