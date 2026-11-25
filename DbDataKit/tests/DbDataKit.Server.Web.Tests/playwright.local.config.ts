// A local run of the stories that need the console only (not committed).
import { defineConfig, devices } from '@playwright/test'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../..')
const scratchDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e')
if (process.env.TEST_WORKER_INDEX === '9999') { fs.rmSync(scratchDirectory, { recursive: true, force: true }); fs.mkdirSync(scratchDirectory, { recursive: true }) }
const origin = 'https://localhost:8481'
export default defineConfig({
  testDir: './tests', timeout: 45_000, fullyParallel: false, workers: 1, retries: 0, reporter: [['list']],
  use: { baseURL: origin, ignoreHTTPSErrors: true, trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [{
    command: ['dotnet', 'exec', path.join(repoRoot, 'DbDataKit/src/DbDataKit.Server/bin/Debug/net10.0/dbdatakit.dll'), 'server', 'run', '--repo', JSON.stringify(scratchDirectory), '--listen', origin,
      '--web-root', JSON.stringify(path.join(repoRoot, 'Shared/web/console/dist'))].join(' '),
    url: `${origin}/health/live`, ignoreHTTPSErrors: true, reuseExistingServer: false, timeout: 60_000, stdout: 'pipe', stderr: 'pipe',
  }],
})
