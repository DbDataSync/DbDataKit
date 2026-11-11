import type { ReactElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { CommandAnswer, TestsResult } from '../types'
import { TestsAnswerView } from './TestsPanel'

const base = { kind: 'metadata' as const, file: 'tests/metadata/x.sql', severity: 'error' as const, columns: [] as string[], rows: [] as Record<string, unknown>[] }
const show = (a: CommandAnswer<TestsResult>) => renderToStaticMarkup(TestsAnswerView({ answer: a }) as ReactElement)

describe('the answer of the project tests', () => {
  it('says every test passed and lists none', () => {
    const text = show({ exit: 0, text: null, document: { ok: true, data: { counts: { total: 2, passed: 2, failed: 0, warned: 0, errored: 0 }, tests: [{ ...base, name: 'naming.a', status: 'pass', violations: 0 }] } } })
    expect(text).toContain('Every test passed.')
    expect(text).toContain('2 run: 2 passed')
    expect(text).not.toContain('build-test-naming.a')
  })
  it('lists a test that failed with its violating rows, and one that could not run with its error', () => {
    const text = show({ exit: 1, text: null, document: { ok: false, data: { counts: { total: 2, passed: 0, failed: 1, warned: 0, errored: 1 }, tests: [
      { ...base, name: 'naming.snake', status: 'fail', violations: 2, columns: ['model', 'column'], rows: [{ model: 'm', column: 'BadName' }] },
      { ...base, name: 'model.x', kind: 'model', status: 'error', violations: 0, error: 'the expected rows do not match the columns' }] } } })
    expect(text).toContain('Some tests did not pass.')
    expect(text).toContain('data-testid="build-test-naming.snake"')
    expect(text).toContain('BadName')
    expect(text).toContain('the expected rows do not match the columns')
  })
  it('says the project has no tests, and shows a failure with no document in words', () => {
    expect(show({ exit: 0, text: null, document: { ok: true, data: { counts: { total: 0, passed: 0, failed: 0, warned: 0, errored: 0 }, tests: [] } } })).toContain('This project has no tests.')
    expect(show({ exit: 2, document: null, text: 'The project could not be read.' })).toContain('The project could not be read.')
  })
})
