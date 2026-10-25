import { defineConfig, devices } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../../..')

// The console's directory for this run: cleared and recreated here, synchronously, before the server that uses it starts. Only in the process that starts the
// run — Playwright evaluates this file again in every worker, and clearing it then would delete the state the server is using.
export const scratchDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e')
const isWorker = process.env.TEST_WORKER_INDEX !== undefined
if (!isWorker) {
  fs.rmSync(scratchDirectory, { recursive: true, force: true })
  fs.mkdirSync(scratchDirectory, { recursive: true })
}

// A Build server for the console to pair with: a real one, in a directory of its own, with a pairing code made before it starts and written where the test can read it.
export const buildDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-build')
export const buildPort = 8482
export const buildUrl = `https://localhost:${buildPort}`
const buildDll = path.join(repoRoot, 'DbDataBuild/src/DbDataBuild.Cli/bin/Debug/net10.0/dbdatabuild.dll')
if (!isWorker) {
  fs.rmSync(buildDirectory, { recursive: true, force: true })
  fs.mkdirSync(buildDirectory, { recursive: true })
  const made = execFileSync('dotnet', ['exec', buildDll, 'server', 'pair', '--repo', buildDirectory, '--max-role', 'viewer'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'build-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// A Sync server too, for the console to host its screens: a real one, https with a certificate exported from the SDK's development certificate (a server must present a certificate to be paired
// with), a pairing code that lets the console grant admin, and a repository of its own.
export const syncDirectory = path.join(os.tmpdir(), 'dbdatakit-console-e2e-sync')
export const syncPort = 8483
export const syncUrl = `https://localhost:${syncPort}`
const syncDll = path.join(repoRoot, 'DbDataSync/src/DbDataSync.Cli/bin/Debug/net10.0/DbDataSync.Cli.dll')
const syncCertificate = path.join(os.tmpdir(), 'dbdatakit-console-e2e-sync.pfx')
const syncCertificatePassword = 'console-e2e'
if (!isWorker) {
  fs.rmSync(syncDirectory, { recursive: true, force: true })
  fs.mkdirSync(syncDirectory, { recursive: true })
  execFileSync('dotnet', ['dev-certs', 'https', '--export-path', syncCertificate, '--password', syncCertificatePassword, '--format', 'Pfx'], { stdio: 'inherit' })
  const made = execFileSync('dotnet', ['exec', syncDll, 'server', 'pair', '--repo', syncDirectory, '--max-role', 'admin'], { encoding: 'utf8' })
  fs.writeFileSync(path.join(scratchDirectory, 'sync-pairing-code.txt'), /Pairing code: (\S+)/.exec(made)![1])
}

// A passkey is bound to the host name and the port it was made on, and the server builds its list of allowed origins from the address it is told to listen on,
// so the address is fixed here and the test opens exactly that.
const port = 8481
const origin = `https://localhost:${port}`

export default defineConfig({
  testDir: './tests',
  timeout: process.env.CI ? 90_000 : 45_000,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: process.env.CI ? [['list'], ['github']] : [['list']],
  use: {
    baseURL: origin,
    ignoreHTTPSErrors: true,   // the console's own self-signed certificate
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
    // `dotnet exec` on the built DLL, not `dotnet run`: killing a wrapper does not reliably kill its child.
    command: [
      'dotnet', 'exec', path.join(repoRoot, 'DbDataConsole/src/DbDataConsole.Server/bin/Debug/net10.0/dbdatakit.dll'),
      'server', 'run', '--repo', JSON.stringify(scratchDirectory), '--listen', origin,
      '--web-root', JSON.stringify(path.join(repoRoot, 'DbDataKit/web/console/dist')),
    ].join(' '),
    url: `${origin}/health/live`,
    ignoreHTTPSErrors: true,
    reuseExistingServer: false,
    timeout: 60_000,
    stdout: 'pipe',
    stderr: 'pipe',
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(buildDll), 'server', 'run', '--repo', JSON.stringify(buildDirectory), '--listen', buildUrl].join(' '),
      url: `${buildUrl}/health/live`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe',
      stderr: 'pipe',
    },
    {
      command: ['dotnet', 'exec', JSON.stringify(syncDll), 'server', 'run', '--repo', JSON.stringify(syncDirectory), '--url', syncUrl].join(' '),
      url: `${syncUrl}/api/health`,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 90_000,
      stdout: 'pipe',
      stderr: 'pipe',
      env: {
        Kestrel__Certificates__Default__Path: syncCertificate,
        Kestrel__Certificates__Default__Password: syncCertificatePassword,
      },
    },
  ],
})
