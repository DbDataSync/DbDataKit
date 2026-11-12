import { describe, expect, it } from 'vitest'
import { NEW_CHOICES, canTest, consentReturn, needsConsent, isConnectionName, keptHere, secretLabel, splitSummary, webhookChoices, webhookYaml, yamlWebhookFormat, outsideDetail, refusedDiagnostics, secretProblems, testSummary, type OutsideConnection, type OutsideTest } from './outsideConnections'
import { ApiError } from '@dbdatakit/shell'

const crm: OutsideConnection = {
  name: 'crm', file: 'connections/crm.yml', yaml: '', kind: 'http', endpoint: 'https://api.example.com/v1', signIn: 'api_key', cloud: null, diagnostics: [],
  secrets: [{ field: 'key', path: 'sign_in.key_secret', reference: 'secret:dbdatakit:server:connection:crm:key', available: false, problem: 'The secret `secret:dbdatakit:server:connection:crm:key` is not set.' }],
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

  it('starts a webhook or a mail server from its own template and labels the secret parts of an address', () => {
    expect(NEW_CHOICES.map((c) => [c.id, c.kind, c.flavor ?? null])).toEqual([['http', 'http', null], ['model-provider', 'model-provider', null], ['smtp', 'smtp', null], ['sftp', 'sftp', null], ['scp', 'scp', null], ['ftps', 'ftps', null], ['smb', 'smb', null], ['s3', 's3', null], ['azure-blob', 'azure-blob', null], ['slack', 'http', 'slack'], ['teams', 'http', 'teams'], ['oauth', 'http', 'oauth'], ['signed', 'http', 'signed']])
    expect(secretLabel('key')).toBe('API key')
    expect(secretLabel('token', 'endpoint_secrets.token')).toBe('Secret part {token} of the address')
    expect(secretLabel('endpoint', 'endpoint')).toBe('The whole address (a secret)')
    expect(keptHere('slack-ops', { field: 'token', path: 'endpoint_secrets.token', reference: 'secret:anything:slack-ops:token', available: true, problem: null, keptHere: true })).toBe(true)
    expect(keptHere('slack-ops', { field: 'token', path: 'endpoint_secrets.token', reference: 'env:SLACK_TOKEN', available: true, problem: null, keptHere: false })).toBe(false)
  })

  it('knows a connection that signs in with consent, and reads the outcome the callback sends the person back with (plan 27a IX03)', () => {
    expect(needsConsent({ signIn: 'oauth2_authorization_code' })).toBe(true)
    expect(needsConsent(crm)).toBe(false)
    expect(secretLabel('refresh_token')).toContain('Refresh token')
    expect(consentReturn('?consent=ok&connection=crm&message=Consent%20was%20given.')).toEqual({ ok: true, connection: 'crm', message: 'Consent was given.' })
    expect(consentReturn('?consent=failed&connection=crm&message=The%20provider%20did%20not%20give%20consent%20(access_denied).')?.ok).toBe(false)
    expect(consentReturn('')).toBeNull()
    expect(consentReturn('?consent=maybe')).toBeNull()
  })

  it('knows the file servers: their labels, the key they sign in with and which kinds Test can try (plan 27a IX04)', () => {
    expect(['http', 'smtp', 'sftp', 'scp', 'ftps', 'smb', 's3', 'azure-blob'].every(canTest)).toBe(true)
    expect(outsideDetail({ ...crm, kind: 's3', endpoint: 's3://reports/incoming/ at https://s3.example.com', signIn: 'access_key' })).toBe('s3://reports/incoming/ at https://s3.example.com, access key (AWS Signature V4)')
    expect(outsideDetail({ ...crm, kind: 'smb', endpoint: 'smb://fs01/data/incoming', signIn: 'kerberos' })).toBe('smb://fs01/data/incoming, Windows sign-in (Kerberos)')
    expect(canTest('model-provider')).toBe(false)
    expect(canTest(null)).toBe(false)
    expect(secretLabel('private_key')).toContain('private key')
    expect(outsideDetail({ ...crm, kind: 'sftp', endpoint: 'sftp://files.example.com:22/upload', signIn: 'ssh_key' })).toBe('sftp://files.example.com:22/upload, SSH private key')
  })

  it('writes the file of a split address with the template and the references, never a part', () => {
    const split = { detected: true, service: 'Slack', format: 'slack', template: 'https://hooks.slack.com/services/{token}', secrets: { token: 'secret:x:ops-slack:token' } }
    const yaml = webhookYaml('ops-slack', split)
    expect(yaml).toBe('schema: connection/2\nkind: http\nname: ops-slack\nendpoint: "https://hooks.slack.com/services/{token}"\nendpoint_secrets:\n  token: secret:x:ops-slack:token\nwebhook:\n  format: slack\n')
    expect(splitSummary(split)).toBe('Recognised as Slack (format slack). The file keeps https://hooks.slack.com/services/{token}; {token} is kept as secret in the console\'s secret store.')
    expect(splitSummary({ ...split, format: null, secrets: { seg2: 'a', key: 'b' } })).toContain('{seg2}, {key} are kept as secrets')
    // an unknown service is a signed webhook, whose key the person adds
    expect(webhookYaml('gen', { ...split, format: null })).toContain('format: signed\n# a signed webhook is signed with a secret')
  })

  it('reads the format a file says and what a pasted address offers for marking, without its values', () => {
    expect(yamlWebhookFormat('kind: http\nwebhook:\n  format: teams\n')).toBe('teams')
    expect(yamlWebhookFormat('kind: http\nwebhook: { format: slack, allow_private: [] }\n')).toBe('slack')
    expect(yamlWebhookFormat('kind: http\n')).toBeNull()
    expect(webhookChoices('https://hooks.example.com/in/abc/x?key=SECRET&page=2')).toEqual({ pathSegments: 3, queryKeys: ['key', 'page'] })
    expect(webhookChoices('https://hooks.example.com')).toEqual({ pathSegments: 0, queryKeys: [] })
    expect(webhookChoices('not an address')).toBeNull()
  })
})
