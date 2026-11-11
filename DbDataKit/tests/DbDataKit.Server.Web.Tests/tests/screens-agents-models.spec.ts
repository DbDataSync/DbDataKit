import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 27a AG18 (bind a slot differently in two channels, reorder a fallback, see usage against the limits), AG19 (the person's memories: list, edit, delete) and AG21 (a tenant-only install, written to the policy and confirmed as a change): the screens with the host mocked in the page.
// The host side is `DbDataKit.Server.Tests` ConsoleModelsTests (the fallback, the policy rule, the memories API) and `DbDataKit.Agents.Tests`. The name sorts after `first-run` and `identity-home`, which the invitation of the first console user needs.

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const provider = (name: string, kind: string, cloud: boolean, model: string) => ({ name, file: `connections/${name}.yml`, yaml: '', kind, endpoint: null, cloud, purpose: 'chat', keyReference: null, keyAvailable: true, keyProblem: null, models: [{ id: model, window: 8000, maxOutput: 4096, priceInput: null, priceOutput: null }], diagnostics: [] })

test('a slot is bound differently in two channels with a fallback reordered, usage is shown against the limits, a person deletes a memory, and a tenant-only policy is reviewed', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Model Tester', `agents-models-${Date.now()}@example.com`)
  const savedSlots: string[] = []
  const savedPolicies: string[] = []
  let memories = [
    { id: 'm1', kind: 'person', agent: 'helper', text: 'Prefers tables.', createdAt: 't', updatedAt: 't' },
    { id: 'm2', kind: 'person', agent: 'helper', text: 'Speaks German.', createdAt: 't', updatedAt: 't' },
  ]
  await page.route('**/api/agents/models', (route) => json(route, {
    providers: [provider('claude', 'anthropic', true, 'claude-sonnet-4-5'), provider('local', 'ollama', false, 'llama3.1:8b')], slotsYaml: '', slotDiagnostics: [], agentSlots: [], secretPrefix: '',
    bindings: [{ slot: 'default', channel: '*', choices: [{ provider: 'claude', model: 'claude-sonnet-4-5', usable: true, problem: null }] }],
  }))
  await page.route('**/api/agents/models/slots', async (route) => { savedSlots.push((route.request().postDataJSON() as { yaml: string }).yaml); await json(route, { saved: true, diagnostics: [] }) })
  await page.route('**/api/agents/usage', (route) => json(route, {
    byPerson: [{ key: 'p', personName: 'Model Tester', turns: 3, inputTokens: 900, outputTokens: 90, cost: 5 }],
    byAgent: [{ key: 'helper', turns: 3, inputTokens: 900, outputTokens: 90, cost: 5 }], install: { key: 'install', turns: 3, inputTokens: 900, outputTokens: 90, cost: 5 },
  }))
  await page.route('**/api/agents/policy', async (route) => {
    if (route.request().method() === 'PUT') { savedPolicies.push((route.request().postDataJSON() as { yaml: string }).yaml); return json(route, { saved: true, changes: [] }) }
    return json(route, { yaml: 'agent-policy: 1\nlimits:\n  install:\n    cost: 20\n', diagnostics: [] })
  })
  await page.route('**/api/agents/policy/preview', (route) => json(route, {
    diagnostics: [], loosens: false,
    changes: [{ setting: 'fallback to cloud providers', loosens: false, text: 'A slot never falls through to a cloud provider: a failing local model ends the conversation with a message instead (a tenant-only install). (fallback: refuse)' }],
  }))
  await page.route('**/api/agents/memories**', async (route) => {
    const request = route.request()
    if (request.method() === 'DELETE') { const id = new URL(request.url()).pathname.split('/').pop()!; memories = memories.filter((m) => m.id !== id); return json(route, { message: 'Forgotten.' }) }
    if (request.method() === 'PUT') return json(route, { message: 'The memory was changed.' })
    return json(route, memories)
  })
  await page.route('**/api/agents/definitions', (route) => json(route, []))
  await page.route('**/api/agents/conversations*', (route) => json(route, []))

  // AG18: the production channel binds the cloud model then the local one; the test channel only the local one; the fallback is reordered
  await page.goto('/agents?tab=models')
  await expect(page.getByTestId('slot-editor')).toBeVisible()
  await expect(page.getByTestId('usage-install')).toContainText('of 20 (25%)')
  await page.getByTestId('bind-add').selectOption('local/llama3.1:8b')
  await expect(page.getByTestId('bind-choice')).toHaveCount(2)
  await page.getByTestId('bind-up-1').click()
  await expect(page.getByTestId('bind-choice').first()).toContainText('local/llama3.1:8b')
  await page.getByTestId('bind-channel').fill('test')
  await expect(page.getByTestId('bind-empty')).toBeVisible()
  await page.getByTestId('bind-add').selectOption('local/llama3.1:8b')
  await page.getByTestId('bind-save').click()
  await expect(page.getByTestId('bind-saved')).toBeVisible()
  expect(savedSlots[0]).toContain('"*":\n      - { provider: local, model: "llama3.1:8b" }\n      - { provider: claude, model: "claude-sonnet-4-5" }')
  expect(savedSlots[0]).toContain('test:\n      - { provider: local, model: "llama3.1:8b" }')

  // AG19: the person's memories are listed; one is deleted, one is edited
  await page.goto('/agents')
  await expect(page.getByTestId('memory')).toHaveCount(2)
  await page.getByTestId('memory-forget-m1').click()
  await expect(page.getByTestId('memory')).toHaveCount(1)
  await page.getByTestId('memory-edit-m2').click()
  await page.getByTestId('memory-input').fill('Speaks German and English.')
  await page.getByTestId('memory-save').click()
  await expect(page.getByTestId('memory-text')).toBeVisible()

  // AG21: a tenant-only install is written into the policy file and described as a change
  await page.goto('/agents?tab=policy')
  await page.getByTestId('policy-tenant-only').check()
  await page.getByTestId('policy-memory-person').uncheck()
  await page.getByTestId('policy-review').click()
  await expect(page.getByTestId('policy-change').first()).toContainText('never falls through to a cloud provider')
  await page.getByTestId('policy-save').click()
  await expect(page.getByTestId('policy-saved')).toBeVisible()
  expect(savedPolicies[0]).toContain('fallback: refuse')
  expect(savedPolicies[0]).toContain('person: off')
})
