import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { parseYaml } from '@dbdatakit/shell'
import { editorKindOf } from '../editors'
import { classNameOf, integrationFilesOf, newTaskFiles, taskNameProblem, tasksOf } from '../integrationSchemas'
import { languageOf } from '../model'
import type { LiveCheck } from '../useLiveCheck'
import { IntegrationManifestEditor, IntegrationSetPanel } from './IntegrationPanel'

const idle: LiveCheck = { diagnostics: [], checkedText: null, running: false, failed: null }
const manifest = [
  'schema: package/1', 'name: orders-pull', 'launch: { kind: assembly, assembly: bin/orders-pull.dll }', 'tasks:',
  '  - name: pull', '    entry: { type: Integration.Pull }', '    schedule: { cron: "0 2 * * *" }', '    connections: [ { name: shop, kind: http } ]',
  '  - name: hello', '    entry: { type: Integration.Hello }', '    triggers:', '      - manual: true', '',
].join('\n')
const paths = ['package.yaml', 'src/pull.csx', 'src/hello.csx', 'tests/hello.csx', 'README.md', '.gitignore']

describe('which editor a file of an integration project gets', () => {
  it('gives the manifest the form and everything else the code editor', () => {
    expect(editorKindOf('integration', 'package.yaml')).toBe('integration-manifest')
    expect(editorKindOf('integration', 'src/pull.csx')).toBeNull()
    expect(editorKindOf('integration', 'files/data.yaml')).toBeNull()
    expect(editorKindOf('build', 'package.yaml')).toBeNull()
  })
  it('colours a csx file as C#', () => {
    expect(languageOf('src/pull.csx')).toBe('csharp')
  })
})

describe('the integration at a glance', () => {
  it('sorts the files into code, tests and the rest', () => {
    expect(integrationFilesOf(paths)).toEqual({ code: ['src/hello.csx', 'src/pull.csx'], tests: ['tests/hello.csx'], other: ['package.yaml', 'README.md', '.gitignore'] })
  })
  it('lists the tasks of the manifest with how each starts and how many slots it has', () => {
    expect(tasksOf(manifest)).toEqual([{ name: 'pull', when: 'cron 0 2 * * *', slots: 1 }, { name: 'hello', when: '1 trigger', slots: 0 }])
    expect(tasksOf('name: [')).toEqual([])
    expect(tasksOf('schema: package/1\nname: x\n')).toEqual([])
  })
  it('refuses a task name that is taken or is not a name', () => {
    expect(taskNameProblem('', ['pull'])).toBeNull()
    expect(taskNameProblem('pull', ['pull'])).toContain('already exists')
    expect(taskNameProblem('Pull It', ['pull'])).toContain('Lower-case')
    expect(taskNameProblem('push-orders', ['pull'])).toBeNull()
  })
  it('names the class of a task in C#', () => {
    expect(classNameOf('pull-orders')).toBe('PullOrders')
    expect(classNameOf('1st')).toBe('Task1st')
  })
  it('adds a task as the manifest entry, a job class and a test, and the manifest still reads', () => {
    const [m, job, test] = newTaskFiles(manifest, 'push-orders')
    expect([m.path, job.path, test.path]).toEqual(['package.yaml', 'src/push-orders.csx', 'tests/push-orders.csx'])
    expect(parseYaml(m.text).errors).toEqual([])
    expect(tasksOf(m.text).map((t) => t.name)).toEqual(['pull', 'hello', 'push-orders'])
    expect(m.text).toContain('Integration.PushOrders')
    expect(m.text).toContain('schema: package/1')
    expect(job.text).toContain('[RunnerTask("push-orders")]')
    expect(job.text).toContain('class PushOrders : RunnerJob<NoInput, NoProgress, NoSlots>')
    expect(test.text).toContain('RunSimulator<Integration.PushOrders>')
  })
  it('draws tasks, code and tests, each opening its file, and the forms only for an author', () => {
    const html = renderToStaticMarkup(<IntegrationSetPanel manifest={manifest} paths={paths} canWrite busy={false} onOpen={() => {}} onCreate={() => {}} onRunTests={() => {}} running={false} />)
    expect(html.match(/data-testid="wsp-int-task"/g)).toHaveLength(2)
    expect(html.match(/data-testid="wsp-int-code"/g)).toHaveLength(2)
    expect(html.match(/data-testid="wsp-int-test"/g)).toHaveLength(1)
    expect(html).toContain('cron 0 2 * * *')
    expect(html).toContain('data-testid="wsp-new-task"')
    expect(html).toContain('data-testid="wsp-int-run-tests"')
    const reader = renderToStaticMarkup(<IntegrationSetPanel manifest={manifest} paths={paths} canWrite={false} busy={false} onOpen={() => {}} onCreate={() => {}} onRunTests={() => {}} running={false} />)
    expect(reader).not.toContain('wsp-new-task')
    expect(reader).toContain('disabled=""')
  })
  it('counts the errors of each file from the findings of the check, not those of a warning', () => {
    const html = renderToStaticMarkup(<IntegrationSetPanel manifest={manifest} paths={paths} canWrite busy={false} onOpen={() => {}} onCreate={() => {}} onRunTests={() => {}} running={false}
      findings={[{ severity: 'error', code: 'DBD-INT-CS1002', message: '; expected', file: 'src/pull.csx', line: 3 }, { severity: 'warning', code: 'DBD-INT-CS8618', message: 'n', file: 'src/hello.csx', line: 1 }]} />)
    expect(html).toMatch(/data-path="src\/pull.csx" data-errors="1"/)
    expect(html).toMatch(/data-path="src\/hello.csx" data-errors="0"/)
    expect(html).toContain('1 to fix')
  })
  it('says so when the manifest cannot be read, and offers no new task then', () => {
    const html = renderToStaticMarkup(<IntegrationSetPanel manifest={null} paths={paths} canWrite busy={false} onOpen={() => {}} onCreate={() => {}} onRunTests={() => {}} running={false} />)
    expect(html).toContain('The manifest could not be read.')
    expect(html).toMatch(/disabled=""[^>]*data-testid="wsp-new-task-go"/)
  })
  it('names the tests run as a build', () => {
    const html = renderToStaticMarkup(<IntegrationSetPanel manifest={manifest} paths={paths} canWrite busy={false} onOpen={() => {}} onCreate={() => {}} onRunTests={() => {}} running />)
    expect(html).toContain('Starting...')
    expect(html).toContain('the tests first, then the package')
  })
})

describe('the manifest as a form', () => {
  it('draws the form from the schema, with the slots note, and the YAML under its own tab', () => {
    const html = renderToStaticMarkup(<IntegrationManifestEditor path="package.yaml" text={manifest} onText={() => {}} readOnly={false} live={idle} />)
    expect(html).toContain('data-testid="wsp-form"')
    expect(html).toContain('data-testid="form-field-name"')
    expect(html).toContain('wsp-int-slots-note')
    expect(html).toContain('dbdatakit:&lt;service&gt;:&lt;kind&gt;:&lt;name&gt;')
    expect(html).toContain('data-testid="wsp-mode-yaml"')
  })
  it('shows the findings of the check about the manifest with their lines', () => {
    const live: LiveCheck = { diagnostics: [{ severity: 'error', code: 'DBD-INT-TASK', message: "package.yaml lists the task 'x' and no job class has it.", file: 'package.yaml', line: 5 }], checkedText: manifest, running: false, failed: null }
    const html = renderToStaticMarkup(<IntegrationManifestEditor path="package.yaml" text={manifest} onText={() => {}} readOnly={false} live={live} />)
    expect(html).toContain('DBD-INT-TASK')
    expect(html).toContain('line 5')
  })
})
