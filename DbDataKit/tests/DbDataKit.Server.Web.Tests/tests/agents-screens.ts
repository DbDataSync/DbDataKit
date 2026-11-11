import { expect, type Page, type Route } from '@playwright/test'

// Plan 27a AG15, AG17, AG29 to AG32: the console's agent screens against a fake agent host, mocked in the page. The host side (the module, the desk that holds a step until its person answers, the policy editor's loosening check, the reports)
// is `DbDataKit.Server.Tests` ConsoleAgentsTests and `DbDataKit.Agents.Tests` HostTests, on the real host with a scripted model; what these stories show is the screens: a step that waits and is answered, a loosening that has to be confirmed, a warning that names a layer.

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const AGENT = { name: 'helper', title: 'Helper', description: null, audience: 'back-end', modelSlot: 'default', problems: 0, modelBound: true }
const row = (over: Record<string, unknown>) => ({ id: 'x', conversationId: 'c1', turn: 1, kind: 'tool-called', person: 'Agent Tester', agent: 'helper', agentVersion: 'v1', origin: 'console', tool: null, arguments: null, detail: null, occurredAt: '2026-11-11T10:00:00Z', ...over })

export interface AgentHostMock {
  answers: { id: string; approve: boolean }[]
  savedPolicies: { yaml: string; confirmed: boolean }[]
}

export async function mockAgentHost(page: Page): Promise<AgentHostMock> {
  const answers: AgentHostMock['answers'] = []
  const savedPolicies: AgentHostMock['savedPolicies'] = []
  let asked = 0
  let pending: Record<string, unknown> | null = null
  const text: { seq: number; role: string; content: string }[] = []
  const audit: Record<string, unknown>[] = [row({ id: 'o1', conversationId: null, origin: 'mcp', agent: 'claude-code', agentVersion: '2.1', kind: 'approval-asked', tool: 'build_model_add', detail: 'command model add orders  [writes-definitions]' })]
  let status = 'open'

  const conversationView = () => ({
    conversation: { id: 'c1', agent: 'helper', agentVersion: 'v1', person: 'p', personName: 'Agent Tester', origin: 'console', channel: null, workspace: null, title: null, status, stopReason: null, startedAt: 't', updatedAt: 't' },
    text: text.map((t) => ({ conversationId: 'c1', createdAt: 't', ...t })),
    turns: asked > 0 && !pending ? [{ id: 't1', conversationId: 'c1', turn: 1, person: 'p', agent: 'helper', agentVersion: 'v1', model: 'scripted', inputTokens: 120, outputTokens: 12, cost: 0, contextPrint: 'ab', contextRecord: 'platform/platform-rules@platform-rules/1, agent/instructions/role.md@a1b2c3', recordedAt: 't' }] : [],
    audit, approvals: pending ? [pending] : [], running: false,
  })

  await page.route('**/api/agents/definitions', (route) => json(route, [AGENT]))
  await page.route('**/api/agents/definitions/helper', (route) => json(route, {
    name: 'helper', file: 'agents/helper/agent.yml', yaml: 'agent: 1\nname: helper\naudience: back-end\nmodel: { slot: default }\ntools:\n  - command: thing edit\n    prompting: do-not-ask\n', diagnostics: [], modelBound: true, instructions: [], tools: ['thing_edit'],
    prompting: [{ tool: 'thing_edit', source: 'command thing edit', traits: ['writes-definitions'], own: 'AskOnce', ownFrom: 'its traits', policyForTool: null, policyMinimum: 'Ask', policyMinimumTrait: 'writes-definitions', agent: 'DoNotAsk', effective: 'Ask', layer: 'install policy: minimum for writes-definitions',
      warning: "The agent's setting 'do-not-ask' for thing_edit is below what the install policy: minimum for writes-definitions needs (ask), so 'ask' applies. Remove the setting or raise it." }],
    space: { agent: 'helper', window: 1000, budgetTokens: 200, totalTokens: 320, over: true, usedShareOfBudget: 1.6, basis: 'an estimate (characters divided by 3.5, plus 15%)', groups: [{ name: 'instructions', tokens: 250, share: 0.25 }, { name: 'tool', tokens: 70, share: 0.07 }],
      parts: [{ name: 'instructions role.md', tokens: 250 }], refusal: "The agent 'helper' gives 320 tokens of instructions, over its budget of 200 tokens. The largest parts are: instructions role.md (250 tokens)." },
    skills: [{ name: 'dbdatabuild', description: 'Build projects', version: 'a1', file: 'skills/dbdatabuild/SKILL.md', bodyTokens: 900, descriptionTokens: 8, fits: false, refusal: "The skill 'dbdatabuild' is 900 tokens and only 400 are left.", exportFiles: { '.claude/skills/dbdatabuild/SKILL.md': '---\nname: dbdatabuild\n---\nbody\n' } }],
  }))
  await page.route('**/api/agents/definitions/helper/check', (route) => json(route, { name: 'helper', file: 'agents/helper/agent.yml', yaml: '', diagnostics: [], prompting: [], space: null, skills: [], instructions: [], tools: [], modelBound: true }))
  await page.route('**/api/agents/conversations*', (route) => route.request().method() === 'POST'
    ? json(route, conversationView().conversation)
    : json(route, [conversationView().conversation]))
  await page.route('**/api/agents/conversations/c1', (route) => json(route, conversationView()))
  await page.route('**/api/agents/conversations/c1/messages', async (route) => {
    const body = route.request().postDataJSON() as { text: string }
    text.push({ seq: text.length + 1, role: 'user', content: body.text })
    asked++
    pending = { id: `a${asked}`, conversationId: 'c1', person: 'p', agent: 'helper', agentVersion: 'v1', origin: 'console', tool: 'thing_deploy', preview: `command thing deploy prod${asked}  [writes-data]`, arguments: null, traits: 'writes-data', status: 'pending', requestedAt: '2026-11-11T10:00:00Z', answeredAt: null, answeredBy: null, note: null }
    await route.fulfill({ status: 202 })
  })
  await page.route('**/api/agents/approvals*', (route) => {
    if (new URL(route.request().url()).pathname.split('/').length > 4) return route.fallback()
    const url = new URL(route.request().url())
    const all = [...(pending ? [pending] : []), { conversationId: null, person: 'p', personName: 'Agent Tester', arguments: null, status: 'pending', requestedAt: '2026-11-11T10:00:00Z', answeredAt: null, answeredBy: null, note: null, id: 'm1', origin: 'mcp', agent: 'claude-code', agentVersion: '2.1', tool: 'build_model_add', preview: 'command model add orders  [writes-definitions]', traits: 'writes-definitions' }]
    return json(route, url.searchParams.get('pending') === 'true' ? all.filter((a) => a.id) : [])
  })
  await page.route('**/api/agents/approvals/*', (route) => {
    const id = route.request().url().split('/').pop()!
    const body = route.request().postDataJSON() as { approve: boolean }
    answers.push({ id, approve: body.approve })
    if (pending && pending.id === id) {
      audit.push(row({ id: `e${id}`, kind: body.approve ? 'approval-approved' : 'approval-denied', tool: 'thing_deploy' }))
      pending = null
      text.push({ seq: text.length + 1, role: 'assistant', content: body.approve ? 'Deployed.' : 'I could not deploy: it was not approved.' })
    }
    return json(route, { id, status: body.approve ? 'approved' : 'denied' })
  })
  await page.route('**/api/agents/audit*', (route) => json(route, audit))
  await page.route('**/api/agents/usage', (route) => json(route, { byPerson: [], byAgent: [], install: { key: 'install', turns: 1, inputTokens: 120, outputTokens: 12, cost: 0 } }))
  await page.route('**/api/agents/policy', async (route) => {
    if (route.request().method() === 'PUT') {
      const body = route.request().postDataJSON() as { yaml: string; confirmed: boolean }
      savedPolicies.push(body)
      return json(route, { saved: true, changes: [] })
    }
    return json(route, { yaml: 'agent-policy: 1\nprompting:\n  minimums:\n    writes-data: ask\n', diagnostics: [] })
  })
  await page.route('**/api/agents/policy/preview', (route) => {
    const body = route.request().postDataJSON() as { yaml: string }
    const loosens = body.yaml.includes('ask-once')
    return json(route, {
      diagnostics: [], loosens,
      changes: loosens ? [{ setting: 'minimum for writes-data', loosens: true, text: 'Lowers the minimum for writes-data from ask to ask-once: a tool that writes data then asks once per conversation instead of every time.' }] : [],
    })
  })
  return { answers, savedPolicies }
}

/** AG15: a message makes a step wait, with the command's own preview and traits; one is approved and one is not; what a turn was given is open. */
export async function chatStory(page: Page, mock: AgentHostMock) {
  await page.goto('/agents')
  await page.getByTestId('chat-agent-select').selectOption('helper')
  await page.getByTestId('chat-start').click()
  await expect(page.getByTestId('chat-conversation')).toBeVisible()

  await page.getByTestId('chat-input').fill('deploy prod1')
  await page.getByTestId('chat-send').click()
  const prompt = page.getByTestId('approval-prompt')
  await expect(prompt).toBeVisible()
  await expect(page.getByTestId('approval-preview')).toContainText('command thing deploy prod1')
  await expect(prompt).toContainText('writes-data')
  await page.getByTestId('approval-approve').click()
  await expect(page.getByTestId('chat-transcript')).toContainText('Deployed.')
  expect(mock.answers[0]).toEqual({ id: 'a1', approve: true })

  await page.getByTestId('chat-input').fill('deploy prod2')
  await page.getByTestId('chat-send').click()
  await page.getByTestId('approval-deny').click()
  await expect(page.getByTestId('chat-transcript')).toContainText('it was not approved')
  expect(mock.answers[1]).toEqual({ id: 'a2', approve: false })

  await page.getByTestId('chat-usage').locator('summary').click()
  await page.getByTestId('chat-turn-context-1').click()
  await expect(page.getByTestId('chat-context')).toContainText('platform/platform-rules@platform-rules/1')
}

/** AG29: the inbox lists an outside agent's step beside the console's, and the record groups what happened by session. */
export async function inboxStory(page: Page, mock: AgentHostMock) {
  await page.goto('/agents?tab=inbox')
  const prompt = page.getByTestId('approval-prompt').filter({ hasText: 'build_model_add' }).or(page.getByTestId('approval-prompt').filter({ hasText: 'model add orders' }))
  await expect(prompt.first()).toBeVisible()
  await prompt.first().getByTestId('approval-approve').click()
  expect(mock.answers.some((a) => a.id === 'm1' && a.approve)).toBe(true)

  await page.goto('/agents?tab=record')
  await expect(page.getByTestId('record-session').first()).toBeVisible()
  await page.getByTestId('record-origin').selectOption('mcp')
  await page.getByTestId('record-session-toggle').first().click()
  await expect(page.getByTestId('record-session-rows')).toContainText('asked')
  await expect(page.getByTestId('usage-install')).toBeVisible()
}

/** AG30, AG31, AG32: a setting below the policy's minimum names the layer, the space is over with the largest part named, a skill that does not fit is refused by name, and exports. */
export async function definitionStory(page: Page) {
  await page.goto('/agents?tab=agents&agent=helper')
  await expect(page.getByTestId('agent-editor')).toBeVisible()
  const tool = page.getByTestId('prompting-row').filter({ hasText: 'thing_edit' })
  await expect(tool).toHaveAttribute('data-effective', 'ask')
  await expect(tool).toContainText('install policy: minimum for writes-definitions')
  await expect(page.getByTestId('prompting-warning')).toContainText("below what the install policy: minimum for writes-definitions needs")
  await expect(page.getByTestId('agent-space')).toHaveAttribute('data-over', 'true')
  await expect(page.getByTestId('space-refusal')).toContainText('instructions role.md (250 tokens)')
  await expect(page.getByTestId('skill-refusal')).toContainText("'dbdatabuild'")
  const download = page.waitForEvent('download')
  await page.getByTestId('skill-export').click()
  expect((await download).suggestedFilename()).toBe('SKILL.md')
}

/** AG17: lowering a minimum shows the effect and asks for a confirmation before it saves; the confirmed save carries the confirmation. */
export async function policyStory(page: Page, mock: AgentHostMock) {
  await page.goto('/agents?tab=policy')
  await expect(page.getByTestId('policy-minimum-writes-data')).toHaveValue('ask')
  await page.getByTestId('policy-minimum-writes-data').selectOption('ask-once')
  await page.getByTestId('policy-review').click()
  await expect(page.getByTestId('policy-change').first()).toContainText('Lowers the minimum for writes-data from ask to ask-once')
  await expect(page.getByTestId('policy-confirm')).toContainText('less strict')
  expect(mock.savedPolicies).toHaveLength(0)
  await page.getByTestId('policy-confirm-save').click()
  await expect(page.getByTestId('policy-saved')).toBeVisible()
  expect(mock.savedPolicies[0].confirmed).toBe(true)
  expect(mock.savedPolicies[0].yaml).toContain('writes-data: ask-once')
}
