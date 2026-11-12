import { expect, type Page, type Route } from '@playwright/test'

// Plan 27a KN10 and KN17: the Knowledge page's tab Check against a fake host mocked in the page. The host side (the strict reader, the catalog, the owners, the budgets of the agents) is `DbDataKit.Knowledge.Cli.Tests` and `DbDataKit.Server.Tests` ConsoleKnowledgeCheckTests on the real code;
// what this story shows is the screen: a project that would be published, one that would be refused with file, line and the agent it would push over its budget, and the same page after the instruction is fixed.

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

export async function mockCheck(page: Page) {
  let fixed = false
  await page.route('**/api/knowledge/overview', (route) => json(route, {
    projects: [{ name: 'install', exists: false, items: 0, chunks: 0, instructions: 0, guardrails: 0, references: 0, failed: [], orphans: [], files: [] }, { name: 'project', exists: true, items: 2, chunks: 2, instructions: 2, guardrails: 0, references: 0, failed: [], orphans: [], files: [] }],
    hasSubjectCatalog: true, subjects: [], index: { built: false, message: 'No index is built.' }, worklists: [],
  }))
  await page.route('**/api/knowledge/check', (route) => json(route, {
    projects: [fixed
      ? { name: 'project', ok: true, items: 2, diagnostics: [], reaches: [{ agent: 'helper', adds: 240, used: 900, budget: 25600, pushesOver: false, items: ['big.md', 'small.md'] }] }
      : {
        name: 'project', ok: false, items: 2,
        diagnostics: [
          { code: 'DBD-KNW-010', severity: 'error', file: 'ghost.md', line: 6, column: 5, message: "'semantic-model:Ghosts' is not known to the catalog.", fix: 'Point it at an object that exists.' },
          { code: 'DBD-KNW-040', severity: 'error', file: 'big.md', line: 7, column: 1, message: "The agent 'helper' would be over its budget of 25600 tokens: it is given 900 before this project and the project adds 30000.", fix: 'Shorten the instruction, or make it a reference.' },
        ],
        reaches: [{ agent: 'helper', adds: 30000, used: 900, budget: 25600, pushesOver: true, items: ['big.md', 'small.md'] }],
      }],
  }))
  return { fix: () => { fixed = true } }
}

/** KN10, KN17: a refused project says file, line and the agent over its budget; after the fix the same page says what would be published. */
export async function checkStory(page: Page, mock: { fix: () => void }) {
  await page.goto('/knowledge?tab=check')
  const card = page.getByTestId('check-project')
  await expect(card).toHaveAttribute('data-ok', 'false')
  await expect(page.getByTestId('check-verdict')).toHaveText('A publish would be refused: 2 errors.')
  await expect(page.getByTestId('check-diagnostic').filter({ hasText: 'ghost.md line 6' })).toContainText('DBD-KNW-010')
  await expect(page.getByTestId('check-diagnostic').filter({ hasText: 'big.md line 7' })).toContainText("The agent 'helper' would be over its budget")
  await expect(page.getByTestId('check-reach')).toHaveAttribute('data-over', 'true')
  await expect(page.getByTestId('check-reach')).toContainText('which is over')

  mock.fix()
  await page.getByTestId('check-run').click()
  await expect(card).toHaveAttribute('data-ok', 'true')
  await expect(page.getByTestId('check-verdict')).toHaveText('2 items would be published.')
  await expect(page.getByTestId('check-diagnostic')).toHaveCount(0)
  await expect(page.getByTestId('check-reach')).toHaveAttribute('data-over', 'false')
}
