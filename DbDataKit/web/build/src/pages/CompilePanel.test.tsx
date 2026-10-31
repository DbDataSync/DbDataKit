import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { CompileView, compileFailure, compiledSentence, type CompileViewProps } from './CompilePanel'
import type { CompileSummary } from '../types'

const counts = { models: 8, errors: 0, warnings: 0, notes: 0, by_kind: {} }
const props = (over: Partial<CompileViewProps> = {}): CompileViewProps => ({ outOfDate: 0, busy: false, done: null, failure: null, onCompile: () => {}, ...over })

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

describe('Compile (write the rendered files)', () => {
  it('says how many files the compile wrote, and when it had nothing to do', () => {
    expect(compiledSentence({ counts, wrote: ['rendered/a', 'rendered/b'], removed: [] })).toBe('Wrote 2 rendered files. They are part of the draft now: commit them with the rest.')
    expect(compiledSentence({ counts, wrote: ['rendered/a'], removed: ['rendered/old'] })).toContain('Wrote 1 rendered file and removed 1.')
    expect(compiledSentence({ counts, wrote: [], removed: [] })).toContain('nothing was written')
    expect(compiledSentence({ counts } as CompileSummary)).toContain('nothing was written')
  })

  it('says how many rendered files the last check found out of date, and stresses the button only then', () => {
    const stale = renderToStaticMarkup(CompileView(props({ outOfDate: 9 })))
    expect(stale).toContain('9 rendered files are out of date.')
    expect(find(CompileView(props({ outOfDate: 9 })), 'build-compile-button')?.props.className).toBe('btn btn-primary')
    expect(find(CompileView(props({ outOfDate: 0 })), 'build-compile-button')?.props.className).toBe('btn')
    expect(renderToStaticMarkup(CompileView(props({ outOfDate: 1 })))).toContain('1 rendered file is out of date.')
    expect(renderToStaticMarkup(CompileView(props()))).toContain('match the models')
    const unknown = renderToStaticMarkup(CompileView(props({ outOfDate: null })))   // before the first check has answered, nothing is claimed either way
    expect(unknown).not.toContain('match the models')
    expect(unknown).not.toContain('out of date')
    expect(find(CompileView(props({ outOfDate: null })), 'build-compile-button')?.props.className).toBe('btn')
  })

  it('is busy while it compiles, and calls back when pressed', () => {
    expect(find(CompileView(props({ busy: true })), 'build-compile-button')?.props.disabled).toBe(true)
    let pressed = 0
    ;(find(CompileView(props({ onCompile: () => pressed++ })), 'build-compile-button')!.props.onClick as () => void)()
    expect(pressed).toBe(1)
  })

  it('shows what it wrote or why it could not, in the tool’s words', () => {
    expect(renderToStaticMarkup(CompileView(props({ done: 'Wrote 2 rendered files.' })))).toContain('build-compile-done')
    expect(renderToStaticMarkup(CompileView(props({ failure: 'DDB-306: x' })))).toContain('build-compile-failed')
    expect(renderToStaticMarkup(CompileView(props()))).not.toContain('build-compile-failed')
    const failed = { exit: 1, text: null, document: { ok: false, data: { counts }, diagnostics: [{ code: 'DDB-306', severity: 'error' as const, title: 't', location: { file: 'models/a.sql', line: 1, column: 1 }, found: 'Unbalanced parenthesis' }] } }
    expect(compileFailure(failed)).toBe('DDB-306: Unbalanced parenthesis')
    expect(compileFailure({ exit: 70, text: 'The command did not answer.\n at X', document: null })).toBe('The command did not answer.')
    expect(compileFailure({ exit: 0, text: null, document: { ok: true, data: { counts } } })).toBeNull()
  })
})
