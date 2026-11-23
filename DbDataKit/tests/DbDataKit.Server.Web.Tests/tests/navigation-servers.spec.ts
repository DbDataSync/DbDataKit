import { expect, test } from '@playwright/test'
import { signInNewAdmin } from './people'

// The paired servers in the console's navigation: each by its name and product under Servers,
// a Workspace's projects under Build and release, and inside a server the console's navigation stays with the server's entries nested under its name and
// the breadcrumb naming it. The console's list of servers and the relay are answered in the page (the servers' own screens are the other stories'), so it
// needs the signed-in console only and takes no port. It signs in a new administrator, so it sorts after `first-run` and `identity-home`.

const servers = [
  { id: 'runner-a', name: 'runner-a', product: 'dbdatarunner' },
  { id: 'gateway-east', name: 'gateway-east', product: 'dbdatagateway' },
  { id: 'gateway-west', name: 'gateway-west', product: 'dbdatagateway' },
  { id: 'gateway-down', name: 'gateway-down', product: 'dbdatagateway', reachable: false },
  { id: 'workspace-a', name: 'workspace-a', product: 'dbdataworkspace' },
].map((s) => ({
  url: `https://${s.id}.example`, version: '1.0.0', apiVersion: 1, pairedAt: '2026-11-18T00:00:00Z', reachable: true, ...s,
  ready: s.reachable !== false, problem: s.reachable === false ? 'connection refused' : null,
}))

test('from Lineage a runner opens in the navigation with its own entries, Approvals is one click away, and two Gateways each open their own screens', async ({ page, context, baseURL }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signInNewAdmin(page, context, baseURL!, 'Navigation Servers Tester', `navigation-servers-${Date.now()}@example.com`)
  await page.route(/\/api\/instances(\?.*)?$/, (route) => route.request().method() === 'GET'
    ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(servers) })
    : route.continue())
  // what the servers' own screens ask for: not this story's subject, so every call is answered "not found" and the screens say so
  await page.route('**/relay/**', (route) => route.fulfill({ status: 404, contentType: 'application/json', body: JSON.stringify({ error: 'answered by the story' }) }))

  await page.goto('/lineage')
  const nav = page.getByTestId('nav')
  for (const s of servers) await expect(page.getByTestId(`nav-server-${s.id}`)).toBeVisible()
  await expect(page.getByTestId('nav-server-runner-a')).toContainText('Runner')
  await expect(page.getByTestId('nav-server-gateway-down')).toHaveAttribute('data-reachable', 'false')
  await expect(page.getByTestId('nav-server-gateway-down')).toContainText('not answering')
  await expect(page.getByTestId('nav-group-build-and-release').getByTestId('rail-projects')).toHaveAttribute('href', '/servers/workspace-a')

  // a runner: its Tasks, Runs and Packages under its name; the breadcrumb names it; Approvals of the console is one click away
  await page.getByTestId('nav-server-runner-a').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/servers/runner-a')
  const nested = page.getByTestId('nav-server-entries')
  await expect(nested.locator('.rail-item')).toHaveText(['Tasks', 'Runs', 'Packages'])
  await expect(page.getByTestId('crumb-server')).toHaveText('runner-a')
  await nested.getByTestId('rail-runs').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/servers/runner-a/runs')
  await expect(nested.getByTestId('rail-runs')).toHaveClass(/\bactive\b/)
  await expect(page.getByTestId('rail-console-runs')).not.toHaveClass(/\bactive\b/)
  await expect(page.getByTestId('rail-home-link')).toHaveCount(0)
  await page.getByTestId('rail-approvals').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/approvals')
  await expect(page.getByTestId('nav-server-entries')).toHaveCount(0)

  // two Gateways: each opens its own screens, and the breadcrumb says which
  await page.getByTestId('nav-server-gateway-east').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/servers/gateway-east')
  await expect(page.getByTestId('crumb-server')).toHaveText('gateway-east')
  await expect(nested.getByTestId('rail-overview')).toBeVisible()
  await nested.getByTestId('rail-data-sources').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/servers/gateway-east/data-sources')
  await expect(page.getByTestId('nav-server-gateway-east')).toHaveClass(/\bactive\b/)
  await expect(nested.locator('.rail-item.active')).toHaveText(['Data sources'])
  await page.getByTestId('nav-server-gateway-west').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/servers/gateway-west')
  await expect(page.getByTestId('crumb-server')).toHaveText('gateway-west')
  await expect(page.getByTestId('nav-server-gateway-east')).not.toHaveClass(/\bactive\b/)
  await expect(nav.getByTestId('nav-server-gateway-west').locator('xpath=following-sibling::*[1]')).toHaveAttribute('data-testid', 'nav-server-entries')
  await nested.getByTestId('rail-data-sources').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/servers/gateway-west/data-sources')

  // the Workspace's projects from Build and release
  await page.getByTestId('rail-projects').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/servers/workspace-a')
  await expect(page.getByTestId('crumb-server')).toHaveText('workspace-a')
  await expect(nested.getByTestId('rail-workspaces')).toBeVisible()
})
