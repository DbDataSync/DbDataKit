import { defineConfig, devices } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'

// Build's web page in a real browser (plan 13a, B-PW). The page is no longer a command of its own (`ui web` is gone): it is what `dbdatabuild ui mcp` serves a host that cannot show an MCP app, through a
// link that works once. Each spec starts its own `ui mcp` process over a copy of the starter project (fixtures.ts), so there is no `webServer` here and no fixed port: the server picks a free one.
// Run: build the CLI (`dotnet build src/DbDataBuild.Cli` in DbDataBuild, after scripts/fetch-native.sh), then `npx playwright test`. The specs run one at a time in the order of their file names.

// A machine that has a Chromium already (the cloud sessions: /opt/pw-browsers) uses it, because the browser this Playwright release wants is not there and `playwright install` is not available. CI installs its own.
function preinstalledChromium(): string | undefined {
  if (process.env.CI) return undefined
  const root = process.env.PLAYWRIGHT_BROWSERS_PATH ?? '/opt/pw-browsers'
  if (!fs.existsSync(root)) return undefined
  const found = fs.readdirSync(root).filter(d => /^chromium-\d+$/.test(d)).sort().reverse()
    .map(d => path.join(root, d, 'chrome-linux64', 'chrome')).find(p => fs.existsSync(p))
  return found
}
const executablePath = preinstalledChromium()

export default defineConfig({
  testDir: './tests',
  outputDir: 'test-results',
  timeout: process.env.CI ? 120_000 : 60_000,
  // a screen runs the project's commands (lowering, lineage, tests) on first use, which takes seconds on a small runner
  expect: { timeout: process.env.CI ? 60_000 : 30_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? [['list'], ['github']] : [['list']],
  use: {
    ...devices['Desktop Chrome'],
    trace: 'retain-on-failure',
    launchOptions: executablePath ? { executablePath, args: ['--no-sandbox'] } : {},
  },
})
