import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 27a AG33: the Approvals page lists the steps agents asked for through a Gateway's `mcp` module (`GET /api/v1/mcp/approvals`) beside the other waiting things, says they are an agent's, and decides them as the signed-in person: a note is optional for
// both answers, what is sent is the decision's address and the note, and a refusal of the Gateway (the requester may not approve their own step) is shown in its words with the row still waiting. The relay is mocked in the page, so the story needs the signed-in
// console only. It signs in a new administrator, so it sorts after `identity-home` and `first-run`.
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
const GW = 'gw-mcp'

test('an agent\'s step waiting on a Gateway is listed as an agent step, approved with a note, refused in the Gateway\'s words once, and denied', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Mcp Approvals Tester', `approvals-mcp-${Date.now()}@example.com`)
  const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString()
  let waiting = [
    { id: 'st-1', status: 'pending', tool: 'publish_dashboard', summary: 'Publish dashboard sales to the Gateway prod', asked_by: 'u-agent', agent: 'planner-agent', arguments: { name: 'sales' }, asked_at: ago(7) },
    { id: 'st-2', status: 'pending', tool: 'drop_table', summary: 'Drop table staging.orders', asked_by: 'u-agent', agent: 'cleanup-agent', arguments: { table: 'staging.orders' }, asked_at: ago(3) },
    { id: 'st-3', status: 'approved', tool: 'old_step', summary: 'Already decided', asked_by: 'u-agent', agent: 'planner-agent', arguments: {}, asked_at: ago(900) },
  ]
  const decisions: { path: string; body: unknown }[] = []
  let refuseOnce = true
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: GW, name: 'Agents gateway', url: 'https://gateway.invalid', product: 'dbdatagateway', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${GW}/api/v1/**`, async (route) => {
    const request = route.request()
    const tail = new URL(request.url()).pathname.slice(`/relay/${GW}/api/v1/`.length)
    if (tail === 'apps') return json(route, { apps: [] })
    if (tail === 'mcp/approvals') return json(route, { approvals: waiting })
    const decision = /^mcp\/approvals\/([^/]+)\/(approve|deny)$/.exec(tail)
    if (decision && request.method() === 'POST') {
      decisions.push({ path: tail, body: request.postDataJSON() })
      if (refuseOnce && decision[1] === 'st-1') { refuseOnce = false; return json(route, { error: 'The agent that asked for a step cannot approve it for itself.' }, 403) }
      waiting = waiting.filter((a) => a.id !== decision[1])
      return json(route, { id: decision[1], status: decision[2] === 'approve' ? 'approved' : 'denied' })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })

  await page.goto('/approvals')
  await expect(page.getByTestId('approvals-page')).toBeVisible()
  const rows = page.getByTestId('approvals-row')
  await expect(rows).toHaveCount(2)                       // the decided step is not a row
  await expect(page.getByTestId('approvals-summary')).toContainText('2 waiting, 2 that you may decide')
  // the longest wait first
  await expect(rows.nth(0)).toContainText('publish_dashboard')
  await expect(rows.nth(0).getByTestId('approvals-kind')).toHaveText('Agent step (an agent)')
  await expect(rows.nth(0)).toContainText('Publish dashboard sales to the Gateway prod')
  await expect(rows.nth(0)).toContainText('planner-agent')
  await expect(rows.nth(0).getByTestId('approvals-waited')).toHaveText('7 min')
  await expect(rows.nth(0)).toHaveAttribute('data-kind', 'mcp-step')
  await expect(rows.nth(1)).toContainText('drop_table')

  // a step may be denied with no note (a change request may not); the note is optional for both
  const first = rows.nth(0)
  await expect(first.getByTestId('approvals-note')).toHaveAttribute('placeholder', 'Note (optional)')
  await expect(first.getByTestId('approvals-approve')).toBeEnabled()
  await expect(first.getByTestId('approvals-reject')).toBeEnabled()
  await expect(first.getByTestId('approvals-why')).toHaveCount(0)

  // the Gateway's refusal is shown in its words and the row stays
  await first.getByTestId('approvals-note').fill('looks right')
  await first.getByTestId('approvals-approve').click()
  await expect(first.getByTestId('approvals-refusal')).toContainText('cannot approve it for itself')
  await expect(rows).toHaveCount(2)
  expect(decisions).toEqual([{ path: 'mcp/approvals/st-1/approve', body: { note: 'looks right' } }])

  // the same step again: approved, the row goes and the summary says one is left
  await first.getByTestId('approvals-approve').click()
  await expect(rows).toHaveCount(1)
  await expect(page.getByTestId('approvals-summary')).toContainText('1 waiting, 1 that you may decide')
  // the other is denied with no note: the body is empty
  await rows.nth(0).getByTestId('approvals-reject').click()
  await expect(page.getByTestId('approvals-none')).toBeVisible()
  expect(decisions.map((d) => d.path)).toEqual(['mcp/approvals/st-1/approve', 'mcp/approvals/st-1/approve', 'mcp/approvals/st-2/deny'])
  expect(decisions[1].body).toEqual({ note: 'looks right' })
  expect(decisions[2].body).toEqual({})
})
