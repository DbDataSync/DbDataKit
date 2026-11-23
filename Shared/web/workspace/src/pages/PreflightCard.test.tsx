// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleProvider } from '@dbdatakit/shell'
import type { StagedRun } from '../api'
import { ProjectRunsPanel } from './ProjectRunsPanel'
import { findingsSummary, preflightFindingsOf, preflightWords } from './PreflightCard'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const run = (steps: unknown[]): StagedRun => ({ id: 'r', task: 'preflight', outcome: 'succeeded', started: '2026-11-23T10:00:00Z', finished: '2026-11-23T10:00:05Z', version: null, failure: null, steps: steps.map((result, i) => ({ key: String(i), started: '', completed: '', result })) })

describe('the preflight of a package', () => {
  it('reads the findings each part recorded, a severity it does not know as an error', () => {
    const found = preflightFindingsOf(run([
      { part: 'status warehouse', findings: [{ severity: 'error', message: 'marts.dim_region is not on the connection.', about: 'warehouse' }, { severity: 'info', message: 'nightly never ran', about: 'warehouse' }] },
      { part: 'status crm', findings: [{ severity: 'fatal', message: 'a newer kind of finding' }] },
    ]))
    expect(found?.map((f) => f.severity)).toEqual(['error', 'info', 'error'])
    expect(findingsSummary(found!)).toBe('The preflight found 2 errors and 0 warnings.')
    expect(preflightFindingsOf(run([{ view: 'status' }]))).toBeNull()
  })

  it('says what became of the activation', () => {
    expect(preflightWords({ declared: false })).toContain('declares no preflight')
    expect(preflightWords({ declared: true, state: 'running' })).toContain('Activate it once the run has ended')
    expect(preflightWords({ declared: true, state: 'finished', errors: 1, activation: 'refused' })).toContain('not activated')
    expect(preflightWords({ declared: true, state: 'finished', errors: 1, activation: 'activated', confirmed_by: 'ana' })).toContain('confirmation is kept')
  })
})

/** The console with a Build draft whose newest deploy put `sales` version 3 on `jobs` in prod, served, with the task `preflight`; Re-check runs it and its run reads back with two findings. */
function fakeConsole() {
  const calls: string[] = []
  const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const method = init?.method ?? 'GET'
    calls.push(`${method} ${url.pathname}${url.search}${init?.body ? ' ' + String(init.body) : ''}`)
    if (url.pathname === '/api/deploys' && method === 'GET') return reply({ deploys: [{ id: 'd1', workspace_server: 'ws-1', project: 'sales', workspace: 'w1', build: 'b1', item: 'package', kind: 'build', target: 'r-1', target_name: 'jobs', product: 'runner', route: 'package',
      name: 'sales', channel: 'prod', version: 3, mode: 'serve', signature: 'verified', deployed_by: 'ana', deployed_at: '', outcome: 'ok', error: null, tasks: ['warehouse-nightly', 'status', 'check', 'preflight'] }] })
    if (url.pathname === '/api/deploys/d1/runs' && method === 'POST') return reply({ steps: [], run: { run_id: 'run-7', task: 'preflight', package: 'sales', channel: 'prod', version: 'served', target: 'jobs', runner: 'https://jobs', instance: 'r-1', on_behalf_of: 'ana' } }, 202)
    if (url.pathname === '/api/deploys/d1/runs/run-7') return reply({ run: run([{ part: 'status warehouse', findings: [
      { severity: 'warning', message: 'marts.dim_store was changed outside dbdatabuild since it was deployed.', about: 'warehouse' },
      { severity: 'error', message: 'The deployed structure is behind the project: 2 deploy step(s) are pending.', about: 'warehouse' }] }]) })
    return reply({ error: 'There is no such call.' }, 404)
  })
  return { calls, fetcher }
}

let root: Root
let host: HTMLDivElement
const settle = async () => { for (let i = 0; i < 12; i++) await act(async () => { await new Promise((r) => setTimeout(r, 5)) }) }
const q = (sel: string) => host.querySelector(sel) as HTMLElement | null

describe('Re-check on the Runs tab', () => {
  let console_: ReturnType<typeof fakeConsole>
  beforeEach(() => { console_ = fakeConsole(); vi.stubGlobal('fetch', console_.fetcher) })
  afterEach(() => { act(() => root.unmount()); host.remove(); vi.unstubAllGlobals() })

  it('runs the preflight of the version the channel serves through the console and shows its findings, errors first', async () => {
    host = document.createElement('div')
    document.body.appendChild(host)
    root = createRoot(host)
    await act(async () => {
      root.render(
        <ModuleProvider info={{ basePath: '', apiBase: '/relay/ws-1', host: { nav: [], serverId: 'ws-1', serverName: 'Workspace' } }}>
          <MemoryRouter><ProjectRunsPanel project="sales" id="w1" kind="build" paths={['connections/warehouse.yaml']} /></MemoryRouter>
        </ModuleProvider>,
      )
    })
    await settle()
    // the preflight is its own line, not one of the tasks Run now offers
    expect(q('[data-testid="wsp-runs-task"][data-task="preflight"]')).toBeNull()
    await act(async () => { q('[data-testid="wsp-runs-preflight-recheck"]')!.click() })
    await settle()

    expect(console_.calls).toContain('POST /api/deploys/d1/runs {"task":"preflight","inputs":{},"served":true}')
    expect(console_.calls).toContain('GET /api/deploys/d1/runs/run-7?step=preflight-findings')
    expect(q('[data-testid="wsp-runs-preflight-result"]')?.dataset.errors).toBe('1')
    expect(q('[data-testid="wsp-runs-preflight-summary"]')?.textContent).toBe('The preflight found 1 error and 1 warning.')
    expect([...host.querySelectorAll('[data-testid="wsp-preflight-finding"]')].map((f) => (f as HTMLElement).dataset.severity)).toEqual(['error', 'warning'])
    expect(q('[data-testid="wsp-runs-run-link"]')?.getAttribute('href')).toBe('/servers/r-1/runs?run=r')
  })
})
