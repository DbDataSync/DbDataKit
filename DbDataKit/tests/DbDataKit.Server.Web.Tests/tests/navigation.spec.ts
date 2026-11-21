import { expect, test, type Page } from '@playwright/test'
import { signInNewAdmin, signInNewPerson } from './people'

// The console's navigation (DbDataKit/README.md, "Finding a screen: the navigation"): Home, six named groups of labelled entries, Docs; every entry opens
// its page with that entry lit; a button collapses it to the icon rail, the browser keeps the choice, and a window under 1000 pixels starts collapsed; a
// viewer sees no administrators' entry and no group left empty. It signs in new people (`user invite`), so it sorts after `first-run` and `identity-home`;
// it needs no server but the console, so it takes no port.

/** The groups and entries of the README, in order: [group title ('' for none), [entry name, test id, address]]. */
const groups: [string, [string, string, string][]][] = [
  ['', [['Home', 'rail-home', '/']]],
  ['Explore', [['Views', 'rail-views', '/views'], ['Lineage', 'rail-lineage', '/lineage'], ['Quality', 'rail-quality', '/quality']]],
  ['Build and release', [['Releases', 'rail-releases', '/releases'], ['Deployments', 'rail-deployments', '/deployments'], ['Backlog', 'rail-backlog', '/backlog']]],
  ['Operate', [['Monitoring', 'rail-monitoring', '/monitoring'], ['Runs', 'rail-runs', '/runs'], ['Approvals', 'rail-approvals', '/approvals'], ['Delivery', 'rail-delivery', '/delivery'], ['Tracking', 'rail-tracking', '/tracking']]],
  ['Agents and knowledge', [['Agents', 'rail-agents', '/agents'], ['Knowledge', 'rail-knowledge', '/knowledge']]],
  ['Servers', [['All servers', 'rail-instances', '/instances'], ['Add a server', 'rail-add-server', '/add-server']]],
  ['Settings', [['Connections', 'rail-connections-all', '/connections'], ['Bindings', 'rail-bindings', '/bindings'], ['Secrets', 'rail-secrets', '/secrets'], ['Users', 'rail-users', '/users'], ['Audit', 'rail-audit', '/audit'], ['Setup', 'rail-setup', '/setup']]],
  ['', [['Docs', 'rail-docs', '/docs']]],
]
const adminOnly = new Set(['rail-instances', 'rail-add-server', 'rail-secrets', 'rail-users', 'rail-audit', 'rail-setup'])

/**
 * What the navigation shows, read from the page: each group's heading and the names of its entries. The servers paired by the stories before this one
 * (each by its name under Servers, a Workspace's Projects) are left out: the console is shared by every story.
 */
async function shown(page: Page) {
  return page.getByTestId('nav').locator(':scope > .rail-group').evaluateAll((els) => els.map((g) => [
    g.querySelector('.rail-group-title')?.textContent ?? '',
    Array.from(g.querySelectorAll(':scope > .rail-item')).filter((a) => !/^(nav-server-|rail-projects)/.test(a.getAttribute('data-testid') ?? '')).map((a) => a.textContent ?? ''),
  ]).filter(([, items]) => items.length > 0))
}

test('an administrator finds every page in its group by its name, each lit when open; the navigation collapses and a viewer sees only theirs', async ({ page, context, baseURL, browser }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signInNewAdmin(page, context, baseURL!, 'Navigation Tester', `navigation-${Date.now()}@example.com`)
  const nav = page.getByTestId('nav')
  await expect(nav).toHaveAttribute('data-collapsed', 'false')
  expect(await shown(page)).toEqual(groups.map(([title, items]) => [title, items.map(([name]) => name)]))
  for (const [title] of groups.filter(([t]) => t)) await expect(page.getByTestId(`nav-group-${title.toLowerCase().replace(/ /g, '-')}`)).toBeVisible()

  // every entry opens its page, lit and the only one lit
  for (const [, items] of groups) {
    for (const [name, testId, address] of items) {
      await page.getByTestId(testId).click()
      await expect.poll(() => new URL(page.url()).pathname, name).toBe(address)
      await expect(page.getByTestId(testId), name).toHaveClass(/\bactive\b/)
      await expect(nav.locator('.rail-item.active')).toHaveCount(1)
    }
  }
  // a page under an entry keeps it lit: a tab of Views, and Upgrades under All servers
  await page.goto('/views/dashboards')
  await expect(page.getByTestId('rail-views')).toHaveClass(/\bactive\b/)
  await page.goto('/upgrades')
  await expect(page.getByTestId('rail-instances')).toHaveClass(/\bactive\b/)

  // collapsed to the icon rail: no names, and the browser keeps it
  await page.getByTestId('nav-toggle').click()
  await expect(nav).toHaveAttribute('data-collapsed', 'true')
  await expect(page.getByTestId('rail-views')).toHaveText('')
  await expect(page.getByTestId('rail-views')).toHaveAttribute('title', 'Views')
  await page.reload()
  await expect(nav).toHaveAttribute('data-collapsed', 'true')
  await page.getByTestId('nav-toggle').click()
  await expect(page.getByTestId('rail-views')).toHaveText('Views')

  // a narrow window starts collapsed, whatever was kept
  await page.setViewportSize({ width: 800, height: 450 })
  await page.reload()
  await expect(nav).toHaveAttribute('data-collapsed', 'true')
  expect((await nav.boundingBox())!.width).toBeLessThan(60)
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.reload()
  await expect(nav).toHaveAttribute('data-collapsed', 'false')

  // a viewer: no administrators' entry, and no group left empty (the Servers group is drawn only for the servers paired, which the console lists)
  const viewerContext = await browser.newContext({ baseURL, ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 } })
  const viewer = await viewerContext.newPage()
  await signInNewPerson(viewer, viewerContext, baseURL!, 'Navigation Viewer', `navigation-viewer-${Date.now()}@example.com`, 'viewer')
  const expected = groups
    .map(([title, items]) => [title, items.filter(([, testId]) => !adminOnly.has(testId)).map(([name]) => name)] as const)
    .filter(([, items]) => items.length > 0)
  expect(await shown(viewer)).toEqual(expected)
  // the Servers group holds only the servers paired before (none when this story runs alone): never All servers or Add a server
  await expect(viewer.getByTestId('nav-group-servers').locator('.rail-item:not([data-testid^="nav-server-"])')).toHaveCount(0)
  for (const testId of adminOnly) await expect(viewer.getByTestId(testId)).toHaveCount(0)
  await viewerContext.close()
})
