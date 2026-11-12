import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { parseYaml } from '@dbdatakit/shell'
import { syncNameProblem } from '../syncSchemas'
import { SKELETONS, SCRIPT_KINDS, newScriptFiles, scriptManifestPath, scriptsOf } from '../syncScripts'
import { ScriptTestView, SyncScriptsPanel } from './SyncScriptsPanel'

const paths = ['config/scripts/upper-name.yaml', 'config/scripts/upper-name.csx', 'config/scripts/clean.yaml', 'config/replications/orders/task.yaml']

const panel = (canWrite: boolean) => renderToStaticMarkup(
  <QueryClientProvider client={new QueryClient()}>
    <SyncScriptsPanel project="orders-set" id="draft" paths={paths} canWrite={canWrite} busy={false} onOpen={() => {}} onCreate={() => {}} />
  </QueryClientProvider>,
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
      <QueryClientProvider client={new QueryClient()}><SyncScriptsPanel project="p" id="i" paths={[]} canWrite busy={false} onOpen={() => {}} onCreate={() => {}} /></QueryClientProvider>,
    )
    expect(html).toContain('wsp-scripts-none')
  })
})

describe('the result of a script test', () => {
  const ok = { ok: true, diagnostics: [], data: { name: 'upper-name', kind: 'sqlColumnExpression', ok: true, source: 'generated sample, ANSI quoting', cases: [{ input: 'Name -> Name', output: 'UPPER("Name")', note: null }, { input: 'Id -> Id', output: null, note: 'left as it would have been' }], log: ['hello'], error: null } }
  it('shows the verdict, each case with its note, and the log', () => {
    const html = renderToStaticMarkup(<ScriptTestView answer={ok} />)
    expect(html).toContain('data-ok="true"')
    expect(html).toContain('upper-name compiled and ran: generated sample, ANSI quoting.')
    expect(html.match(/data-testid="wsp-script-case"/g)).toHaveLength(2)
    expect(html).toContain('(none)')
    expect(html).toContain('left as it would have been')
    expect(html).toContain('wsp-script-log')
  })
  it('shows the compiler\'s words when the script did not compile', () => {
    const html = renderToStaticMarkup(<ScriptTestView answer={{ ok: true, diagnostics: [], data: { ...ok.data, ok: false, cases: [], log: [], error: "Script 'x' did not compile: (1,1) nope" } }} />)
    expect(html).toContain('data-ok="false"')
    expect(html).toContain('did not compile: (1,1) nope')
    expect(html).not.toContain('wsp-script-case')
  })
  it('shows the findings when the program did not answer', () => {
    const html = renderToStaticMarkup(<ScriptTestView answer={{ ok: false, diagnostics: [{ severity: 'error', code: 'DBD-WS-SYNC-FAILED', message: 'dbdatasync stopped', file: null, line: null }], data: null }} />)
    expect(html).toContain('DBD-WS-SYNC-FAILED: dbdatasync stopped')
    expect(html).toContain('data-ok="false"')
  })
})
