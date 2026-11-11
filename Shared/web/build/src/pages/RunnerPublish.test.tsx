import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import type { CommandAnswer, PairedRunner, RunnerPublishData } from '../types'
import { RunnerPublishView, type RunnerPublishViewProps } from './RunnerPublish'
import { emptyRunnerForm, modeText, parseBindings, runnerPublishBlock, runnerPublishFailure, runnerPublishRequest, taskLine, type RunnerForm } from './runnerPublish'

const runner = (over: Partial<PairedRunner> = {}): PairedRunner => ({ name: 'runner-x', url: 'https://runner:7443', instance_id: 'r-1', roles: ['viewer', 'operator', 'publisher'], paired_at: '2026-11-01T10:00:00Z', ...over })
const filled = (over: Partial<RunnerForm> = {}): RunnerForm => ({ ...emptyRunnerForm('sales'), runner: 'runner-x', channel: 'prod', ...over })

const data: RunnerPublishData = {
  package: 'sales', channel: 'prod', mode: 'stage', version: 2, changed: true, uploaded: 3, skipped: 1, signature: '', files: 4,
  tasks: [{ name: 'refresh-sqlserver-hourly', connection: 'sqlserver', plan: 'hourly', schedule: '15 * * * *', time_zone: 'UTC', slots: ['sqlserver'], timeout_seconds: null }],
  owner_old: ['refresh-sqlserver-hourly'], bound: [],
}
const ok = (d: RunnerPublishData = data): CommandAnswer<RunnerPublishData> => ({ exit: 0, document: { ok: true, data: d }, text: null })

function props(over: Partial<RunnerPublishViewProps> = {}): RunnerPublishViewProps {
  return { runners: [runner()], loading: false, listError: null, form: filled(), allowed: true, busy: false, answer: undefined, failure: null, onForm: () => {}, onPublish: () => {}, ...over }
}
const html = (over?: Partial<RunnerPublishViewProps>) => renderToStaticMarkup(RunnerPublishView(props(over)))

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

describe('the bindings box', () => {
  it('reads one slot=connection per line and names the first line that is not one', () => {
    expect(parseBindings('a=b\n\n  c = d \r\n')).toEqual({ binds: ['a=b', 'c=d'], problem: null })
    expect(parseBindings('').binds).toEqual([])
    expect(parseBindings('a=b\nnonsense').problem).toBe('"nonsense" is not slot=connection.')
    expect(parseBindings('=x').problem).not.toBeNull()
    expect(parseBindings('x=').problem).not.toBeNull()
  })
})

describe('what stops the button, said in words', () => {
  it('asks for what is missing in order, and for the role the server says is missing', () => {
    const block = (over: Partial<RunnerForm>, allowed: boolean | undefined = true) => runnerPublishBlock(filled(over), [runner()], allowed)
    expect(block({}, false)).toBe('Publishing needs the author role on this server.')
    expect(block({ runner: '' })).toBe('Choose the runner.')
    expect(block({ channel: ' ' })).toBe('Name the channel.')
    expect(block({ packageName: '' })).toBe('Name the package.')
    expect(block({ bindText: 'x' })).toContain('not slot=connection')
    expect(block({})).toBeNull()
    expect(block({}, undefined)).toBeNull()      // the commands list has not answered: the server decides again
  })

  it('knows a pairing without the publisher role cannot publish and one without admin cannot promote', () => {
    expect(runnerPublishBlock(filled(), [runner({ roles: ['viewer'] })], true)).toContain('publisher role')
    expect(runnerPublishBlock(filled({ mode: 'promote', promoteFrom: 'test' }), [runner()], true)).toContain('administrator pairing')
    expect(runnerPublishBlock(filled({ mode: 'promote', promoteFrom: 'test' }), [runner({ roles: ['admin', 'operator'] })], true)).toBeNull()
    expect(runnerPublishBlock(filled({ mode: 'promote', promoteFrom: '' }), [runner({ roles: ['admin'] })], true)).toContain('whose version')
    expect(runnerPublishBlock(filled({ mode: 'promote', promoteFrom: 'prod' }), [runner({ roles: ['admin'] })], true)).toContain('same')
  })
})

describe('the request', () => {
  it('stages by default, sends the bindings and the package, and leaves the plans with this server', () => {
    expect(runnerPublishRequest(filled({ bindText: 'sqlserver=erp' }))).toEqual({ server: 'runner-x', channel: 'prod', packageName: 'sales', serve: false, bind: ['sqlserver=erp'], runnerSchedules: false })
  })
  it('serves and lets the runner schedule only when asked, and neither for a promotion', () => {
    expect(runnerPublishRequest(filled({ serve: true, runnerSchedules: true }))).toMatchObject({ serve: true, runnerSchedules: true })
    expect(runnerPublishRequest(filled({ mode: 'promote', promoteFrom: 'test', serve: true, runnerSchedules: true }))).toMatchObject({ promoteFrom: 'test', serve: false, runnerSchedules: false })
  })
})

describe('what a refusal says', () => {
  it('uses the command\'s errors and diagnostics, then its text, then the exit code', () => {
    expect(runnerPublishFailure(null, ok())).toBeNull()
    expect(runnerPublishFailure(null, { exit: 1, document: { ok: false, data: data, errors: ['The runner refused to set the owner (403).'] }, text: null })).toBe('The runner refused to set the owner (403).')
    expect(runnerPublishFailure(null, { exit: 1, document: { ok: false, data, diagnostics: [{ code: 'DDB-106', severity: 'error', title: 'bad cron', location: { file: 'loads.yml', line: 3, column: 1 }, found: '', supported: '', fix: 'Use five fields.' }] }, text: null })).toBe('loads.yml: bad cron Use five fields.')
    expect(runnerPublishFailure(null, { exit: 2, document: null, text: 'Name the runner' })).toBe('Name the runner')
    expect(runnerPublishFailure(null, { exit: 3, document: null, text: null })).toContain('exit code 3')
  })
  it('says the role in the server\'s words, and a plain sentence for no answer', () => {
    expect(runnerPublishFailure(new ApiError(403, '`project publish` needs the author role.', null), undefined)).toBe('`project publish` needs the author role.')
    expect(runnerPublishFailure(new ApiError(403, 'no', null), undefined)).toContain('author role')
    expect(runnerPublishFailure(new Error('x'), undefined)).toBe('The server did not answer. Try again.')
  })
})

describe('the staged version', () => {
  it('says staged and waiting for the runners, or served, or promoted, and whether anything changed', () => {
    expect(modeText(data)).toContain('is staged on prod')
    expect(modeText({ ...data, mode: 'serve', changed: false })).toContain('is served on prod')
    expect(modeText({ ...data, mode: 'serve', changed: false })).toContain('nothing changed')
    expect(modeText({ ...data, promoted_from: 'test' })).toContain('promoted from test')
    expect(taskLine(data.tasks[0])).toBe('15 * * * * (UTC); connections: sqlserver')
    expect(taskLine({ ...data.tasks[0], schedule: null, slots: [] })).toBe('no schedule; no connection slots')
  })
})

describe('the runner section', () => {
  it('says what it does and offers the paired runners', () => {
    const text = html()
    expect(text).toContain('staged')
    expect(text).toContain('data-testid="build-publish-runner-server"')
    expect(text).toContain('>runner-x</option>')
    expect(text).not.toContain('build-publish-runner-from')
  })
  it('says how to pair when there is no runner, and shows loading and not the empty message while the list is on its way', () => {
    expect(html({ runners: [] })).toContain('data-testid="build-publish-runner-none"')
    expect(html({ runners: [] })).not.toContain('build-publish-runner-button')
    expect(html({ runners: undefined, loading: true })).not.toContain('build-publish-runner-none')
    expect(html({ runners: undefined, loading: true })).toContain('Loading')
  })
  it('shows the second channel only when promoting', () => {
    expect(html({ form: filled({ mode: 'promote' }) })).toContain('build-publish-runner-from')
    expect(html({ form: filled({ mode: 'promote' }) })).not.toContain('build-publish-runner-serve-now')
  })
  it('disables the button and says why, for a missing choice, a missing role and a publish under way', () => {
    const button = (over: Partial<RunnerPublishViewProps>) => find(RunnerPublishView(props(over)), 'build-publish-runner-button')!.props
    expect(button({}).disabled).toBe(false)
    expect(button({ form: filled({ runner: '' }) }).disabled).toBe(true)
    expect(html({ form: filled({ runner: '' }) })).toContain('Choose the runner.')
    expect(button({ allowed: false }).disabled).toBe(true)
    expect(html({ allowed: false })).toContain('author role')
    expect(button({ busy: true }).disabled).toBe(true)
    expect(html({ busy: true })).toContain('Publishing…')
  })
  it('calls publish when pressed', () => {
    let pressed = 0
    ;(find(RunnerPublishView(props({ onPublish: () => { pressed++ } })), 'build-publish-runner-button')!.props.onClick as () => void)()
    expect(pressed).toBe(1)
  })
  it('shows the version, its counts, its tasks and that this server still schedules them', () => {
    const text = html({ answer: ok() })
    expect(text).toContain('Version 2 of sales is staged on prod')
    expect(text).toContain('3 file(s) sent, 1 already on the runner')
    expect(text).toContain('data-task="refresh-sqlserver-hourly"')
    expect(text).toContain('15 * * * *')
    expect(text).toContain('Still scheduled by this server until cut over')
  })
  it('shows the refusal and no success card', () => {
    const text = html({ answer: { exit: 1, document: { ok: false, data, errors: ['Nothing was sent.'] }, text: null } })
    expect(text).toContain('data-testid="build-publish-runner-failed"')
    expect(text).toContain('Nothing was sent.')
    expect(text).not.toContain('build-publish-runner-done')
  })
  it('shows a failed list in the banner', () => {
    expect(html({ runners: undefined, listError: new ApiError(500, 'The pairing file is broken', null) })).toContain('The pairing file is broken')
  })
})
