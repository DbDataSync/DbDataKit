import { expect, test, type Route } from '@playwright/test'
import { signInNewAdmin } from './people'

// Plan 22a GP18 fold (owner decision 2026-11-11): the delivery channels are connections (their binding per release channel is `x-bindings.spec.ts`). This story mocks the outside-connections API in the page (the host side is `DbDataKit.Server.Tests` ConsoleDeliveryTests and
// OutsideConnectionsTests, `DbDataKit.Delivery.Tests` ConnectionChannelTests), so it needs the signed-in console only. It invites a console administrator as `identity-admin.spec.ts` does; its name sorts after `first-run` and `identity-home`.
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

test('a webhook is started from a template, its pasted address is split and only the template is shown', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Webhook Tester', `webhook-${Date.now()}@example.com`)
  const TEMPLATE = 'schema: connection/2\nkind: http\nname: ops-slack\nendpoint: https://hooks.slack.com/services/{token}\nendpoint_secrets:\n  token: secret:x:ops-slack:token\nwebhook:\n  format: slack\n'
  const pasted: unknown[] = []
  await page.route('**/api/outside-connections', (route) => json(route, { kinds: ['http', 'model-provider', 'smtp'], flavors: ['slack', 'teams', 'signed'], connections: [] }))
  await page.route('**/api/outside-connections/template*', (route) => json(route, { yaml: TEMPLATE }))
  await page.route('**/api/outside-connections/ops-slack/webhook', (route) => {
    pasted.push(route.request().postDataJSON())
    return json(route, { detected: true, service: 'Slack', format: 'slack', template: 'https://hooks.slack.com/services/{token}', secrets: { token: 'secret:x:ops-slack:token' } })
  })

  await page.goto('/connections')
  await expect(page.getByTestId('outside-connections')).toBeVisible()
  await page.getByTestId('outside-new-name').fill('ops-slack')
  await page.getByTestId('outside-new-kind').selectOption('slack')
  await expect(page.getByTestId('outside-new-meaning')).toContainText('Slack incoming webhook')
  await page.getByTestId('outside-new-start').click()
  await expect(page.getByTestId('outside-editor')).toBeVisible()
  await expect(page.getByTestId('outside-webhook')).toBeVisible()

  // the address is typed into a password field, split on the server, and the editor then holds the template; no part of the address is on the page
  const url = 'https://hooks.slack.com/services/T000/B000/XXXXCANARYXXXX'
  await expect(page.getByTestId('outside-webhook-url')).toHaveAttribute('type', 'password')
  await page.getByTestId('outside-webhook-url').fill(url)
  await page.getByTestId('outside-webhook-split').click()
  await expect(page.getByTestId('outside-webhook-detected')).toHaveAttribute('data-service', 'Slack')
  await expect(page.getByTestId('outside-webhook-detected')).toContainText('https://hooks.slack.com/services/{token}')
  expect(pasted).toEqual([{ url }])
  await expect(page.getByText('XXXXCANARYXXXX')).toHaveCount(0)
  await expect(page.getByTestId('outside-yaml').locator('.monaco-editor')).toContainText('endpoint_secrets')

})
