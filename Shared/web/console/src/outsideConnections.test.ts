import { describe, expect, it } from 'vitest'
import { isConnectionName, keptHere, outsideDetail, refusedDiagnostics, secretProblems, testSummary, type OutsideConnection, type OutsideTest } from './outsideConnections'
import { ApiError } from '@dbdatakit/shell'

const crm: OutsideConnection = {
  name: 'crm', file: 'connections/crm.yml', yaml: '', kind: 'http', endpoint: 'https://api.example.com/v1', signIn: 'api_key', cloud: null, diagnostics: [],
  secrets: [{ field: 'key', path: 'sign_in.key_secret', reference: 'secret:dbdata:console:connection:crm:key', available: false, problem: 'The secret `secret:dbdata:console:connection:crm:key` is not set.' }],
}

describe('outside connections (plan 27a IX01)', () => {
  it('names a connection by the format\'s rule', () => {
    expect(['crm', 'Crm-2', 'a.b_c'].every(isConnectionName)).toBe(true)
    expect(['', '1crm', 'a b', '../x', 'x'.repeat(65)].some(isConnectionName)).toBe(false)
  })

  it('says in a line the address, the sign-in and for a provider cloud or local', () => {
    expect(outsideDetail(crm)).toBe('https://api.example.com/v1, API key')
    expect(outsideDetail({ ...crm, kind: 'model-provider', signIn: 'none', cloud: false, endpoint: 'http://localhost:11434/v1' })).toBe('http://localhost:11434/v1, no sign-in, local')
    expect(outsideDetail({ ...crm, kind: 'model-provider', cloud: true })).toContain('cloud')
    expect(outsideDetail({ ...crm, kind: null, endpoint: null })).toBe('the file does not read')
  })

  it('lists the secrets that cannot be read and offers typing only for a reference the console keeps', () => {
    expect(secretProblems(crm)).toHaveLength(1)
    expect(secretProblems({ ...crm, secrets: [{ ...crm.secrets[0], available: true }] })).toEqual([])
    expect(keptHere('crm', crm.secrets[0])).toBe(true)
    expect(keptHere('other', crm.secrets[0])).toBe(false)
    expect(keptHere('crm', { ...crm.secrets[0], reference: 'env:CRM_KEY' })).toBe(false)
  })

  it('reads the diagnostics a refused save carries', () => {
    const diagnostics = [{ code: 'DBD-CONN-013', severity: 'error', file: 'connections/x.yml', line: 2, column: 1, message: 'unknown kind', fix: null }]
    expect(refusedDiagnostics({ body: { diagnostics } })).toEqual(diagnostics)
    expect(refusedDiagnostics(new Error('no'))).toEqual([])
    expect(refusedDiagnostics(null)).toEqual([])
    expect(ApiError).toBeDefined()
  })

  it('says what Test found in a line: reached or not, the sign-in, and whether a retry can help', () => {
    const base: OutsideTest = { ok: true, reached: true, signIn: 'accepted', message: 'x', kind: null, temporary: false, status: 200, milliseconds: 5 }
    expect(testSummary(base)).toBe('Works: reached, sign-in accepted.')
    expect(testSummary({ ...base, ok: false, signIn: 'rejected', kind: 'Login', status: 401 })).toBe('Does not work: reached, sign-in rejected. A retry will not help.')
    expect(testSummary({ ...base, ok: false, reached: false, signIn: 'not checked', kind: 'Network', temporary: true, status: null })).toBe('Does not work: not reached, sign-in not checked. A retry can help.')
    expect(testSummary({ ...base, ok: false, reached: null, signIn: 'not checked', kind: null })).toBe('Does not work: not reached or not tried, sign-in not checked.')
  })
})
