import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { StagedVersionView, stagedText } from './StagedVersion'

const base = { package: 'orders', channel: 'dev', mode: 'stage', version: 2, changed: true, tasks: [{ name: 'load', detail: '15 * * * *' }, { name: 'check' }] }

describe('the card of a published version', () => {
  it('says staged versions wait for the runners, served ones are served, and a repeat changed nothing', () => {
    expect(stagedText(base)).toContain('is staged on dev')
    expect(stagedText({ ...base, mode: 'serve' })).toBe('Version 2 of orders is served on dev.')
    expect(stagedText({ ...base, changed: false })).toContain('nothing changed')
    expect(stagedText({ ...base, promotedFrom: 'test' })).toBe('Version 2 of orders is served on dev (promoted from test).')
  })
  it('names its test ids from the prefix and lists the tasks with their detail', () => {
    const html = renderToStaticMarkup(<StagedVersionView data={{ ...base, uploaded: 3, skipped: 1, oldScheduled: ['load'], bound: ['source'] }} testIdPrefix="x" />)
    for (const id of ['x-done', 'x-summary', 'x-counts', 'x-tasks', 'x-task', 'x-old', 'x-bound']) expect(html).toContain(`data-testid="${id}"`)
    expect(html).toContain('3 file(s) sent, 1 already on the runner.')
    expect(html).toContain('15 * * * *')
    expect(html).toContain('Bound in dev: source.')
  })
  it('leaves out what it was not given: no counts for a promotion, no old or bound lines', () => {
    const html = renderToStaticMarkup(<StagedVersionView data={{ ...base, promotedFrom: 'test' }} testIdPrefix="x" />)
    expect(html).not.toContain('x-counts')
    expect(html).not.toContain('x-old')
    expect(html).not.toContain('x-bound')
  })
})
