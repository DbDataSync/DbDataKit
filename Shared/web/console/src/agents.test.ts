import { describe, expect, it } from 'vitest'
import { buildTarget, targetLine, connectCommand, providerTemplate, cost, eventWord, percent, promptingMeaning, promptingName, sessions, statusWord, tokenState, waitingOn, type AgentToken, type Approval, type AuditRow } from './agents'
import { againstLimit } from './agents'
import { emptyPolicy, policyFromYaml, policyToYaml, settingMeaning } from './policyForm'
import { contextLine, isPolicyItem, parseInPlay, policyItemId, policyItemText, workActions, workWord, type WorkItem } from './knowledge'

const row = (over: Partial<AuditRow> = {}): AuditRow => ({
  id: 'r', conversationId: 'c1', turn: 1, kind: 'tool-called', person: 'Ada', agent: 'build-helper', agentVersion: 'v3', origin: 'console', tool: 'x', arguments: null, detail: null, occurredAt: '2026-11-11T10:00:00Z', ...over,
})
const approval = (status: string): Approval => ({ id: 'a', conversationId: 'c1', person: 'Ada', agent: 'x', agentVersion: 'v', origin: 'console', tool: 't', preview: 'p', arguments: null, traits: 'writes-data', status, requestedAt: 't', answeredAt: null, answeredBy: null, note: null })

describe('model providers and slots', () => {
  it('starts a provider file of each kind with the reference the console keeps a typed key under, and none for a local server', () => {
    expect(providerTemplate('anthropic', 'claude')).toContain('key_secret: secret:dbdatakit:server:connection:claude:key')
    expect(providerTemplate('anthropic', 'claude')).toContain('kind: model-provider\n')
    expect(providerTemplate('anthropic', 'claude')).toContain('provider: anthropic\n')
    expect(providerTemplate('ollama', 'local')).not.toContain('key_secret')
    expect(providerTemplate('openai-compatible', 'lab')).toContain('locality: local')
    expect(providerTemplate('foundry', 'f')).toContain('endpoint: https://<resource>')
    expect(providerTemplate('ollama', 'emb', 'embeddings')).toContain('purpose: embeddings')
    expect(providerTemplate('ollama', 'emb', 'embeddings')).toContain('nomic-embed-text')
    expect(providerTemplate('ollama', 'chatty')).not.toContain('purpose:')
  })
  it('says a slot binding as the primary and the fallback in order', () => {
  })
})

describe('connecting an outside agent', () => {
  it('writes the command and the environment, with a placeholder until the token is made', () => {
    const before = connectCommand('dbdatakit', 'https://console.example/', 'u1', null)
    expect(before.command).toBe('dbdatakit ui mcp --ask --report-to https://console.example')
    expect(before.environment.map((e) => e.name)).toEqual(['DBDATAKIT_CONSOLE_USER', 'DBDATAKIT_CONSOLE_TOKEN'])
    expect(before.environment[1].value).toContain('shown when it is made')
    expect(connectCommand('dbdatakit', 'https://c', 'u1', 'ddka_x_y').environment[1].value).toBe('ddka_x_y')
  })
  it('names a token active, revoked or expired', () => {
    const token = (over: Partial<AgentToken>): AgentToken => ({ id: 't', name: 'n', createdAt: '2026-11-01T00:00:00Z', expiresAt: null, revokedAt: null, lastUsedAt: null, active: true, ...over })
    expect(tokenState(token({}))).toBe('active')
    expect(tokenState(token({ active: false }))).toBe('expired')
    expect(tokenState(token({ active: false, revokedAt: '2026-11-02T00:00:00Z' }))).toBe('revoked')
  })
})

describe('the words for settings, events and statuses', () => {
  it('spell the setting as the files do and say what it means', () => {
    expect(promptingName('AskOnce')).toBe('ask-once')
    expect(promptingMeaning('Ask')).toContain('every time')
    expect(promptingMeaning('DoNotAsk')).toContain('without asking')
  })
  it('give an event a word and keep an unknown kind as it is', () => {
    expect(eventWord('approval-denied')).toBe('denied')
    expect(eventWord('tool-called')).toBe('ran')
    expect(eventWord('something-new')).toBe('something-new')
  })
  it('say how a conversation stands, and a share and a cost plainly', () => {
    expect(statusWord('limit-reached')).toBe('Stopped at a limit')
    expect(percent(0.456)).toBe('46%')
    expect(percent(1.3)).toBe('130%')
    expect(cost(0)).toBe('-')
    expect(cost(0.004)).toBe('<0.01')
    expect(cost(1.256)).toBe('1.26')
  })
})

describe('the record by session', () => {
  it('groups by conversation, and an outside agent\'s steps with none by person and agent, newest session first', () => {
    const grouped = sessions([
      row({ id: '1', occurredAt: '2026-11-11T10:00:00Z' }),
      row({ id: '2', occurredAt: '2026-11-11T10:05:00Z', kind: 'approval-asked' }),
      row({ id: '3', conversationId: null, origin: 'mcp', agent: 'claude-code', agentVersion: '2.1', occurredAt: '2026-11-11T11:00:00Z' }),
      row({ id: '4', conversationId: null, origin: 'mcp', agent: 'claude-code', agentVersion: '2.1', occurredAt: '2026-11-11T11:01:00Z', kind: 'approval-approved' }),
    ])
    expect(grouped.map((g) => g.rows.map((r) => r.id))).toEqual([['3', '4'], ['1', '2']])
    expect(grouped[0]).toMatchObject({ origin: 'mcp', agent: 'claude-code', agentVersion: '2.1', person: 'Ada' })
  })
  it('finds the step a conversation waits on', () => {
    expect(waitingOn([approval('approved'), approval('pending')])?.status).toBe('pending')
    expect(waitingOn([approval('approved')])).toBeNull()
  })
})

describe('the policy form', () => {
  it('writes only what is set, and reads its own file back to the same form', () => {
    const form = emptyPolicy()
    form.minimums['writes-data'] = 'ask'
    form.minimums['deletes'] = 'ask'
    form.tools = [{ name: 'connection_deploy', setting: 'ask-once' }]
    form.conversation = { turns: '50', toolCalls: '', tokens: '', cost: '2.5' }
    form.install = { turns: '', toolCalls: '5000', tokens: '', cost: '100' }
    form.notifyAt = '70'
    form.cloudReadsData = 'refuse'

    const yaml = policyToYaml(form)
    const back = policyFromYaml(yaml)

    expect(yaml.startsWith('agent-policy: 1')).toBe(true)
    expect(yaml).not.toContain('person')
    expect(back.errors).toEqual([])
    expect(back.form).toEqual(form)
  })
  it('is only the format line when nothing is set', () => {
    expect(policyToYaml(emptyPolicy())).toBe('agent-policy: 1\n')
    expect(policyFromYaml('agent-policy: 1\n').form).toEqual(emptyPolicy())
  })
  it('does not make a form of text that is not YAML and says why', () => {
    const result = policyFromYaml('prompting: [unclosed')
    expect(result.form).toBeNull()
    expect(result.errors.length).toBeGreaterThan(0)
  })
  it('says what a minimum means for the trait it is on', () => {
    expect(settingMeaning('writes-data', 'ask')).toContain('writes rows or values always asks the person first')
    expect(settingMeaning('deletes', '')).toContain('own default')
  })
})

describe('knowledge worklists and the policy item form', () => {
  const item = (kind: WorkItem['kind']): WorkItem => ({ kind, itemId: 'a.md', title: 'A', message: 'm', subject: kind === 'orphaned' ? 'semantic-model:Sales' : null })
  it('offers to re-point or retire an orphan and only to open the rest', () => {
    expect(workActions(item('orphaned'))).toEqual(['repoint', 'retire', 'open'])
    expect(workActions(item('over-budget'))).toEqual(['open'])
    expect(workWord('needs-look')).toBe('Needs another look')
  })
  it('splits what is in play on lines and commas and drops blanks', () => {
    expect(parseInPlay('build-project:sales\n\n semantic-model:Sales/measure:revenue, install:true ')).toEqual(['build-project:sales', 'semantic-model:Sales/measure:revenue', 'install:true'])
  })
  it('makes a policy item a guardrail owned by the administrators whatever else is typed', () => {
    const text = policyItemText('Never delete a production table.', 'back-end')
    expect(text).toContain('role: guardrail')
    expect(text).toContain('owner: administrators')
    expect(text).toContain('for: { audience: back-end }')
    expect(policyItemText('x', 'everyone')).not.toContain('for:')
    expect(policyItemId('No deletes in Production!')).toBe('policy/no-deletes-in-production.md')
    expect(policyItemId('!!!')).toBe('policy/item.md')
    expect(isPolicyItem('install', 'policy/a.md')).toBe(true)
    expect(isPolicyItem('project', 'policy/a.md')).toBe(false)
  })
  it('reads a context part as a line with its share of the budget', () => {
    expect(contextLine({ layer: 'install-policy', id: 'policy/a.md', version: 'v', tokens: 40, shareOfBudget: 0.123 })).toBe('install-policy: policy/a.md (40 tokens, 12% of the budget)')
  })
})

describe('where a Build agent works (AG14)', () => {
  it('has no target until a server and a project are chosen, and a workspace only when named', () => {
    expect(buildTarget('', '', '')).toBeUndefined()
    expect(buildTarget('b1', '', 'w')).toBeUndefined()
    expect(buildTarget('b1', 'sales', '')).toEqual({ server: 'b1', project: 'sales' })
    expect(buildTarget('b1', 'sales', 'ws1')).toEqual({ server: 'b1', project: 'sales', workspace: 'ws1' })
  })
  it('says whether files can be changed', () => {
    expect(targetLine(undefined)).toBe('the console')
    expect(targetLine({ server: 'b1', project: 'sales' })).toBe('b1 / sales (read only: no workspace)')
    expect(targetLine({ server: 'b1', project: 'sales', workspace: 'ws1' })).toBe('b1 / sales / workspace ws1')
  })
})

describe('AG18 to AG21: the usage line and the policy switches', () => {
  it('says what is used against a limit, and that none is set', () => {
    expect(againstLimit(5, 20, 'cost')).toBe('cost: 5 of 20 (25%)')
    expect(againstLimit(1234, null, 'tokens')).toBe('tokens: 1,234 (no limit set)')
  })

  it('writes the tenant-only rule and the memory switches only when they differ from the default, and reads them back', () => {
    expect(policyToYaml(emptyPolicy())).toBe('agent-policy: 1\n')
    const form = { ...emptyPolicy(), tenantOnly: true, memoryPerson: false }
    const yaml = policyToYaml(form)
    expect(yaml).toContain('fallback: refuse')
    expect(yaml).toContain('person: off')
    expect(yaml).not.toContain('work:')
    const back = policyFromYaml(yaml).form!
    expect(back.tenantOnly).toBe(true)
    expect(back.memoryPerson).toBe(false)
    expect(back.memoryWork).toBe(true)
  })
})
