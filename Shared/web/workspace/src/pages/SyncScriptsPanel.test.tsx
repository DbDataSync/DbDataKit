import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { parseYaml } from '@dbdatakit/shell'
import { syncNameProblem } from '../syncSchemas'
import { SKELETONS, SCRIPT_KINDS, newScriptFiles, scriptManifestPath, scriptsOf } from '../syncScripts'
import { ScriptTestView, SyncScriptsPanel } from './SyncScriptsPanel'
import type { BuildSummary, ScriptTest } from '../api'
import type { RunAnswer } from '../authoringReads'
import type { DeployBody } from '../deploys'
import { channelProblem, defaultScriptTestPlace, scriptTestProductionWarning, testScriptOnRunner, type ScriptTestCalls, type ScriptTestPhase } from '../scriptTestOnRunner'

const paths = ['config/scripts/upper-name.yaml', 'config/scripts/upper-name.csx', 'config/scripts/clean.yaml', 'config/replications/orders/task.yaml']

const panel = (canWrite: boolean) => renderToStaticMarkup(
  <MemoryRouter><QueryClientProvider client={new QueryClient()}>
    <SyncScriptsPanel project="orders-set" id="draft" paths={paths} canWrite={canWrite} busy={false} onOpen={() => {}} onCreate={() => {}} />
  </QueryClientProvider></MemoryRouter>,
)

describe('the scripts of a Sync draft', () => {
  it('are the manifests in config/scripts, by name', () => {
    expect(scriptsOf(paths)).toEqual(['clean', 'upper-name'])
    expect(scriptsOf(['README.md'])).toEqual([])
  })
  it('make a new script as a manifest in the current format and the code of its slot, refusing a taken or malformed name', () => {
    for (const kind of SCRIPT_KINDS) {
      const [manifest, code] = newScriptFiles('mine', kind)
      expect(manifest.path).toBe('config/scripts/mine.yaml')
      expect(code.path).toBe('config/scripts/mine.csx')
      const parsed = parseYaml(manifest.text)
      expect(parsed.errors).toEqual([])
      expect(parsed.value).toMatchObject({ schemaVersion: 1, name: 'mine', kind, language: 'CSharp', enabled: true })
      expect(code.text.trimEnd().endsWith('new MyScript()')).toBe(true)
    }
    expect(SKELETONS.rowTransform).toContain('IRowTransform')
    expect(syncNameProblem('clean', paths, scriptManifestPath)).toContain('already exists')
    expect(syncNameProblem('a b', paths, scriptManifestPath)).toContain('Letters')
  })
  it('are drawn with a Test button each, and the form for a new one only for an author', () => {
    const html = panel(true)
    expect(html.match(/data-testid="wsp-script"/g)).toHaveLength(2)
    expect(html).toContain('data-testid="wsp-new-script"')
    const reader = panel(false)
    expect(reader).not.toContain('wsp-new-script')
    expect(reader).toMatch(/data-testid="wsp-script-test"[^>]*disabled|disabled=""[^>]*data-testid="wsp-script-test"/)
  })
  it('says there is none when the folder is empty', () => {
    const html = renderToStaticMarkup(
      <MemoryRouter><QueryClientProvider client={new QueryClient()}><SyncScriptsPanel project="p" id="i" paths={[]} canWrite busy={false} onOpen={() => {}} onCreate={() => {}} /></QueryClientProvider></MemoryRouter>,
    )
    expect(html).toContain('wsp-scripts-none')
  })
})

describe('the result of a script test', () => {
  const ok: ScriptTest = { name: 'upper-name', kind: 'sqlColumnExpression', ok: true, source: 'generated sample, ANSI quoting', cases: [{ input: 'Name -> Name', output: 'UPPER("Name")', note: null }, { input: 'Id -> Id', output: null, note: 'left as it would have been' }], log: ['hello'], message: null }
  const run = { run_id: 'r-7', link: '/servers/r-1/runs/r-7', runner_name: 'jobs', channel: 'dev', build: 'b-3', version: 4 }
  it('shows the verdict, each case with its note, the log, and the run on the Runner', () => {
    const html = renderToStaticMarkup(<MemoryRouter><ScriptTestView test={ok} run={run} /></MemoryRouter>)
    expect(html).toContain('data-ok="true"')
    expect(html).toContain('upper-name compiled and ran: generated sample, ANSI quoting.')
    expect(html.match(/data-testid="wsp-script-case"/g)).toHaveLength(2)
    expect(html).toContain('(none)')
    expect(html).toContain('left as it would have been')
    expect(html).toContain('wsp-script-log')
    expect(html).toContain('Build b-3, version 4 in dev')
    expect(html).toMatch(/href="\/servers\/r-1\/runs\/r-7"[^>]*data-testid="wsp-script-run-link"|data-testid="wsp-script-run-link"[^>]*href="\/servers\/r-1\/runs\/r-7"/)
  })
  it('shows the compiler\'s words when the script did not compile', () => {
    const html = renderToStaticMarkup(<MemoryRouter><ScriptTestView test={{ ...ok, ok: false, cases: [], log: [], message: "Script 'x' did not compile: (1,1) nope" }} run={run} /></MemoryRouter>)
    expect(html).toContain('data-ok="false"')
    expect(html).toContain('did not compile: (1,1) nope')
    expect(html).not.toContain('wsp-script-case')
  })
})

describe('a script tested on a Runner', () => {
  const place = { workspaceServer: 'ws-1', project: 'orders-set', workspace: 'draft', runner: 'r-1', channel: 'dev' }
  const answer: ScriptTest = { name: 'upper-name', kind: 'sqlColumnExpression', ok: true, source: 'generated sample', cases: [{ input: 'Name -> Name', output: 'UPPER("Name")', note: null }], log: [], message: null }
  const summary = (status: string, message: string | null = null): BuildSummary => ({ id: 'b-3', workspace: 'draft', status, commit: null, requested_by: 'ana', message, requested: '', started: null, finished: null })
  const runAnswer = (state: RunAnswer<ScriptTest>['state'], extra: Partial<RunAnswer<ScriptTest>> = {}): RunAnswer<ScriptTest> =>
    ({ state, run_id: 'r-7', runner: 'r-1', runner_name: 'jobs', channel: 'dev', production: false, link: '/servers/r-1/runs/r-7', ...extra })
  const fake = (over: Partial<ScriptTestCalls> = {}) => {
    const said: string[] = []
    const deployed: DeployBody[] = []
    const asked: Record<string, string>[] = []
    const builds = [summary('queued'), summary('running'), summary('succeeded')]
    const calls: ScriptTestCalls = {
      requestBuild: async () => builds.shift()!,
      build: async () => ({ build: builds.shift()!, result: null }),
      deploy: async (body) => { deployed.push(body); return { steps: [], deployed: {} as never, deploy: { id: 'dep-1', version: 4 } as never } },
      answer: async (_d, task, inputs) => { said.push(task); asked.push(inputs); return runAnswer('running') },
      followAnswer: async () => runAnswer('done', { answer }),
      wait: async () => {},
      ...over,
    }
    return { calls, said, deployed, asked }
  }

  it('builds the draft, stages the build in the channel without serving it, runs script-test there, and answers the cases with the run', async () => {
    const { calls, said, deployed, asked } = fake()
    const phases: ScriptTestPhase[] = []
    const done = await testScriptOnRunner(calls, place, 'upper-name', 3, (p) => phases.push(p))
    expect(phases).toEqual(['building', 'staging', 'running'])
    expect(deployed).toEqual([{ workspaceServer: 'ws-1', project: 'orders-set', workspace: 'draft', build: 'b-3', target: 'r-1', channel: 'dev', serve: false, note: 'Test of the script upper-name' }])
    expect(said).toEqual(['script-test'])
    expect(asked).toEqual([{ name: 'upper-name', rows: '3' }])
    expect(done.test.cases[0]!.output).toBe('UPPER("Name")')
    expect(done).toMatchObject({ run_id: 'r-7', link: '/servers/r-1/runs/r-7', runner_name: 'jobs', build: 'b-3', version: 4 })
  })

  it('stages in production as in any other channel, and the warning says what it replaces there', async () => {
    const { calls, deployed } = fake()
    await testScriptOnRunner(calls, { ...place, channel: 'prod' }, 'upper-name', 5, () => {})
    expect(deployed.map((d) => [d.channel, d.serve])).toEqual([['prod', false]])
    expect(channelProblem('prod')).toBeNull()
    expect(channelProblem('dev')).toBeNull()
    expect(channelProblem('Dev X')).toContain('lower-case')
    expect(scriptTestProductionWarning('prod', 'jobs')).toBe('This stages the draft in production (prod on jobs): it replaces the version staged there, not the one production serves. The script runs against generated input and opens no connection.')
  })

  it('stops with the build\'s reason when the draft does not build, and with the run\'s when it fails', async () => {
    const failed = fake({ requestBuild: async () => summary('failed', 'config/replications/orders: no source cache') })
    await expect(testScriptOnRunner(failed.calls, place, 'upper-name', 5, () => {})).rejects.toThrow('did not build, so the script was not tested: config/replications/orders: no source cache')
    expect(failed.deployed).toEqual([])
    const refused = fake({ answer: async () => runAnswer('failed', { error: "The input `name` names a script of config/scripts/" }) })
    await expect(testScriptOnRunner(refused.calls, place, 'upper-name', 5, () => {})).rejects.toThrow('The input `name` names a script')
  })

  it('is staged where the draft was last deployed to a Runner, production included, else in dev', () => {
    const version = (channel: string) => ({ deploy: 'd', build: 'b', target: 'r-2', targetName: 'etl', package: 'p', channel, version: '1', mode: 'stage', tasks: [] })
    expect(defaultScriptTestPlace(version('test'), [{ id: 'r-1' }])).toEqual({ runner: 'r-2', channel: 'test' })
    expect(defaultScriptTestPlace(version('prod'), [{ id: 'r-1' }])).toEqual({ runner: 'r-2', channel: 'prod' })
    expect(defaultScriptTestPlace(null, [{ id: 'r-1' }])).toEqual({ runner: 'r-1', channel: 'dev' })
    expect(defaultScriptTestPlace(null, [])).toBeNull()
  })
})
