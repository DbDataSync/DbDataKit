import { expect, type Page, type Route } from '@playwright/test'

// Plan 27a AG14, AG15: the console's screens for the build-helper agent against a mocked host. The host side (the relay as the person, what is offered, the approval of a write, a denied write that changes nothing) is
// `DbDataKit.Server.Tests` ConsoleBuildAgentTests on the real host with a scripted model and a fake Build server; what this story shows is the screens: making the agent from a server, choosing a server, project and
// workspace, and what the chat says it works on.

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

export async function buildHelperStory(page: Page): Promise<void> {
  const started: Record<string, unknown>[] = []
  const installed: Record<string, unknown>[] = []
  const helper = { name: 'build-helper', title: 'Build helper', description: null, audience: 'back-end', modelSlot: 'default', problems: 0, modelBound: true }
  await page.route('**/api/agents/build-targets*', (route) => {
    const url = new URL(route.request().url())
    const server = url.searchParams.get('server')
    const project = url.searchParams.get('project')
    return json(route, {
      servers: [{ id: 'build-1', name: 'Main Build' }],
      projects: server ? [{ id: 'sales' }] : null,
      workspaces: project ? [{ id: 'ws1', name: 'my branch' }] : null,
    })
  })
  await page.route('**/api/agents/definitions', (route) => json(route, [helper]))
  await page.route('**/api/agents/build-helper/install', async (route) => {
    installed.push(route.request().postDataJSON())
    await json(route, { name: 'build-helper', file: 'agents/build-helper/agent.yml', yaml: 'agent: 1\n', diagnostics: [], prompting: [], space: null, skills: [], instructions: [], tools: [], modelBound: true })
  })
  await page.route('**/api/agents/conversations', async (route) => {
    if (route.request().method() !== 'POST') return json(route, [])
    started.push(route.request().postDataJSON())
    await json(route, { id: 'c9', agent: 'build-helper', agentVersion: 'v1', person: 'p', origin: 'console', channel: null, workspace: 'ws1', title: null, status: 'open', stopReason: null, startedAt: 't', updatedAt: 't' })
  })
  await page.route('**/api/agents/conversations/c9', (route) => json(route, {
    conversation: { id: 'c9', agent: 'build-helper', agentVersion: 'v1', person: 'p', personName: 'Agent Tester', origin: 'console', channel: null, workspace: 'ws1', title: null, status: 'open', stopReason: null, startedAt: 't', updatedAt: 't' },
    text: [], turns: [], audit: [], approvals: [], running: false,
  }))

  // make the agent from a server
  await page.goto('/agents?tab=agents')
  await page.getByTestId('build-helper-install').getByTestId('build-server').selectOption('build-1')
  await page.getByTestId('build-helper-install').getByTestId('build-project').selectOption('sales')
  await page.getByTestId('build-helper-make').click()
  await expect(page.getByTestId('build-helper-done')).toBeVisible()
  expect(installed).toEqual([{ server: 'build-1', project: 'sales' }])

  // start a conversation on a project, in a workspace: the chat says where it works, and read only until a workspace is chosen
  await page.goto('/agents?tab=chat')
  await page.getByTestId('chat-agent-select').selectOption('build-helper')
  await page.getByTestId('build-server').selectOption('build-1')
  await page.getByTestId('build-project').selectOption('sales')
  await expect(page.getByTestId('chat-target-line')).toContainText('build-1 / sales (read only: no workspace)')
  await page.getByTestId('build-workspace').selectOption('ws1')
  await expect(page.getByTestId('chat-target-line')).toContainText('workspace ws1')
  await page.getByTestId('chat-start').click()
  await expect(page.getByTestId('chat-conversation')).toBeVisible()
  expect(started).toEqual([{ agent: 'build-helper', server: 'build-1', project: 'sales', workspace: 'ws1' }])
}
